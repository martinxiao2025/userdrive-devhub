using System.Text;
using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Providers;
using DevHubd.Core.Queue;
using DevHubd.Core.Scheduling;
using DevHubd.Core.Tasks;
using DevHubd.Core.Workspaces;
using DevHubd.Mcp.Abstractions;
using DevHubd.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// P1 调度流水线验收：同一文件重复扫描只入队一次、内容变更生成新 dedupe_key、多工作区隔离，
/// 以及 <c>dispatch_now</c> 背后的判定语义。
/// </summary>
public sealed class SchedulerPipelineTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteConnectionFactory _factory;
    private readonly QueueService _queue;
    private readonly SchedulerStateStore _stateStore;
    private readonly SchedulerEngine _engine;
    private readonly WorkspaceRegistry _registry;

    /// <summary>准备临时数据目录、数据库、工作区清单与调度引擎。</summary>
    public SchedulerPipelineTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _registry = new WorkspaceRegistry(Path.Combine(_directory, "workspaces.json"));
        _queue = new QueueService(_factory);
        _stateStore = new SchedulerStateStore(_factory);
        _engine = new SchedulerEngine(
            _registry,
            new TaskScanner(_factory),
            _queue,
            _stateStore,
            NullLogger<SchedulerEngine>.Instance);
    }

    /// <summary>清理临时目录；SQLite 连接池可能短暂占用文件，失败不视为测试失败。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }
    }

    /// <summary>同一任务文件扫描 100 次，队列中始终只有 1 项。</summary>
    [Fact]
    public async Task TickAsync_RepeatedScans_KeepSingleQueueItemAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "第一次", "ready", 10);

        for (var index = 0; index < 100; index++)
        {
            await _engine.TickAsync(ct);
        }

        Assert.Equal(1, await CountQueueItemsAsync(ct));
        var state = await _stateStore.ReadAsync(ct);
        Assert.Equal(100, state.Counters["scanned"]);
        Assert.Equal(1, state.Counters["enqueued"]);
    }

    /// <summary>任务完成后内容变更：生成新的 dedupe_key 与新的队列项。</summary>
    [Fact]
    public async Task TickAsync_AfterContentChange_CreatesNewDedupeKeyAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "第一次", "ready", 10);

        await _engine.TickAsync(ct);
        var firstKeys = await ReadDedupeKeysAsync(ct);
        Assert.Single(firstKeys);

        // 模拟上一轮已完成，否则"同一任务已有活动项"会正确阻止重复入队。
        await ExecuteAsync("UPDATE queue_item SET status = 'succeeded';", ct);

        WriteTask(root, "T-001", "第二次（内容已改）", "ready", 10);
        await _engine.TickAsync(ct);

        var secondKeys = await ReadDedupeKeysAsync(ct);
        Assert.Equal(2, secondKeys.Count);
        Assert.Equal(2, secondKeys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>两个工作区存在同名任务：task_view 与队列项都按 workspace 隔离。</summary>
    [Fact]
    public async Task TickAsync_TwoWorkspaces_WithSameTaskId_StayIsolatedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var alpha = CreateWorkspace("alpha");
        var beta = CreateWorkspace("beta");
        WriteWorkspaces(("alpha", alpha), ("beta", beta));
        WriteTask(alpha, "T-001", "Alpha 的任务", "ready", 10);
        WriteTask(beta, "T-001", "Beta 的任务", "ready", 10);

        await _engine.TickAsync(ct);

        Assert.Equal(2, await CountQueueItemsAsync(ct));

        var workspaces = await ReadColumnAsync("SELECT workspace_id FROM queue_item ORDER BY workspace_id;", ct);
        Assert.Equal(new[] { "alpha", "beta" }, workspaces);

        var alphaTask = await _queue.GetTaskAsync("alpha", "T-001", ct);
        var betaTask = await _queue.GetTaskAsync("beta", "T-001", ct);
        Assert.Equal("Alpha 的任务", alphaTask!.Title);
        Assert.Equal("Beta 的任务", betaTask!.Title);
    }

    /// <summary>同名任务跨工作区时，未指定 workspaceId 的手动派发必须返回歧义与候选列表。</summary>
    [Fact]
    public async Task DispatchNow_AmbiguousTask_ReturnsCandidatesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var alpha = CreateWorkspace("alpha");
        var beta = CreateWorkspace("beta");
        WriteWorkspaces(("alpha", alpha), ("beta", beta));
        WriteTask(alpha, "T-001", "Alpha 的任务", "ready", 10);
        WriteTask(beta, "T-001", "Beta 的任务", "ready", 10);
        await _engine.TickAsync(ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", null, "auto", dryRun: false, force: false, ct);

        Assert.Equal(DispatchStatus.AmbiguousTask, outcome.Status);
        Assert.NotNull(outcome.Candidates);
        Assert.Equal(new[] { "alpha", "beta" }, outcome.Candidates!.OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>任务尚未入队时，指定 workspaceId 的手动派发会入队，provider=auto 解析为 openai。</summary>
    [Fact]
    public async Task DispatchNow_WithWorkspace_EnqueuesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "唯一任务", "ready", 10);

        // 只扫描不调度：task_view 有行、队列为空，用于验证手动派发的新增路径。
        await new TaskScanner(_factory).ScanAsync(new WorkspaceDefinition("alpha", root, true), ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "auto", dryRun: false, force: false, ct);

        Assert.Equal(DispatchStatus.Enqueued, outcome.Status);
        Assert.Equal("alpha", outcome.WorkspaceId);
        Assert.Equal("openai", outcome.Provider);
        Assert.False(string.IsNullOrEmpty(outcome.QueueItemId));
        Assert.Equal(1, await CountQueueItemsAsync(ct));
    }

    /// <summary>调度已把同一内容的队列项入队后，手动派发是幂等的：返回 Deduped 而不是重复插入。</summary>
    [Fact]
    public async Task DispatchNow_WhenAlreadyEnqueued_ReturnsDedupedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "唯一任务", "ready", 10);
        await _engine.TickAsync(ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "auto", dryRun: false, force: false, ct);

        Assert.Equal(DispatchStatus.Deduped, outcome.Status);
        Assert.Equal("openai", outcome.Provider);
        Assert.Equal(1, await CountQueueItemsAsync(ct));
    }

    /// <summary>任务不是 ready 时拒绝派发。</summary>
    [Fact]
    public async Task DispatchNow_TaskNotReady_ReturnsTaskNotReadyAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "被阻塞的任务", "blocked", 10);
        await _engine.TickAsync(ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "openai", dryRun: true, force: false, ct);

        Assert.Equal(DispatchStatus.TaskNotReady, outcome.Status);
    }

    /// <summary>provider 非法时返回 InvalidProvider，不做任何写入。</summary>
    [Fact]
    public async Task DispatchNow_InvalidProvider_ReturnsInvalidProviderAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "唯一任务", "ready", 10);
        await _engine.TickAsync(ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "claude", dryRun: false, force: false, ct);

        Assert.Equal(DispatchStatus.InvalidProvider, outcome.Status);
    }

    /// <summary>调度暂停时手动派发返回 SchedulerPaused。</summary>
    [Fact]
    public async Task DispatchNow_WhenPaused_ReturnsSchedulerPausedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "唯一任务", "ready", 10);
        await _stateStore.SetEnabledAsync(false, "manual", ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "openai", dryRun: false, force: false, ct);

        Assert.Equal(DispatchStatus.SchedulerPaused, outcome.Status);
    }

    /// <summary>创建工作区根目录（含 .devhub/tasks）。</summary>
    /// <param name="id">工作区 id。</param>
    private string CreateWorkspace(string id)
    {
        var root = Path.Combine(_directory, id);
        Directory.CreateDirectory(Path.Combine(root, ".devhub", "tasks"));
        return root;
    }

    /// <summary>写入 workspaces.json。</summary>
    /// <param name="entries">(id, root) 列表。</param>
    private void WriteWorkspaces(params (string Id, string Root)[] entries)
    {
        var payload = new
        {
            version = 1,
            workspaces = entries.Select(entry => new { id = entry.Id, root = entry.Root, enabled = true }).ToArray(),
        };

        File.WriteAllText(
            Path.Combine(_directory, "workspaces.json"),
            JsonSerializer.Serialize(payload),
            new UTF8Encoding(false));
    }

    /// <summary>写入一个任务文件。</summary>
    /// <param name="workspaceRoot">工作区根目录。</param>
    /// <param name="id">任务 id。</param>
    /// <param name="title">标题。</param>
    /// <param name="status">状态。</param>
    /// <param name="priority">优先级。</param>
    private static void WriteTask(string workspaceRoot, string id, string title, string status, int priority)
    {
        var payload = new
        {
            id,
            title,
            status,
            priority,
            delivery = "review",
            writeScope = Array.Empty<string>(),
            prompt = "执行该任务",
        };

        File.WriteAllText(
            Path.Combine(workspaceRoot, ".devhub", "tasks", $"{id}.json"),
            JsonSerializer.Serialize(payload),
            new UTF8Encoding(false));
    }

    /// <summary>统计队列项数量。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<int> CountQueueItemsAsync(CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM queue_item;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>读取全部 dedupe_key。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<List<string>> ReadDedupeKeysAsync(CancellationToken cancellationToken) =>
        (await ReadColumnAsync("SELECT dedupe_key FROM queue_item ORDER BY created_at, id;", cancellationToken))
        .ToList();

    /// <summary>读取单列字符串结果。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<IReadOnlyList<string>> ReadColumnAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>执行一条写语句。</summary>
    /// <param name="sql">语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
