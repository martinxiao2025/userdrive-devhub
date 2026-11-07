using System.Text;
using System.Text.Json;
using DevHubd.Core;
using DevHubd.Core.Data;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using DevHubd.Core.Queue;
using DevHubd.Core.Scheduling;
using DevHubd.Core.Tasks;
using DevHubd.Core.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// P2 执行闭环验收（设计文档 §P2 的 5 条）。全部使用 <see cref="FakeModelClient"/>，
/// 只证明编排、预算、路径与熔断语义；真实 OpenAI 调用因缺少凭据标注为未验证。
/// </summary>
public sealed class P2ExecutionTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteConnectionFactory _factory;
    private readonly WorkspaceRegistry _registry;
    private readonly QueueService _queue;
    private readonly ExecutionStore _store;
    private readonly ProviderStateStore _providerStates;
    private readonly TaskScanner _scanner;

    /// <summary>准备独立临时数据库与执行依赖。</summary>
    public P2ExecutionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _registry = new WorkspaceRegistry(Path.Combine(_directory, "workspaces.json"));
        _queue = new QueueService(_factory);
        _store = new ExecutionStore(_factory);
        _providerStates = new ProviderStateStore(_factory);
        _scanner = new TaskScanner(_factory);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }
    }

    /// <summary>
    /// 验收 1：ready 任务在一个 tick 内被派发执行，产出 awaiting_review 结果，
    /// 且日志能用 queueItemId → attempt → turnId 关联。
    /// </summary>
    [Fact]
    public async Task Pump_ReadyTask_RunsTurnAndWritesAwaitingReviewResultAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "写一份报告", "ready", 10);
        await PrepareQueueAsync(ct);

        var turnLogger = new FakeLogger<TurnRunner>();
        var coordinatorLogger = new FakeLogger<ExecutionCoordinator>();
        var fake = new FakeModelClient(
            "openai",
            (_, _, _) => Task.FromResult(TurnResult.Done("已完成报告。", 0, new TokenUsage(10, 5, false), Array.Empty<string>())));

        var coordinator = CreateCoordinator(fake, new ExecutionOptions(), turnLogger, coordinatorLogger);

        var started = await coordinator.PumpAsync(ct);
        Assert.Equal(1, started);
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1, ct);

        var outcome = await ScalarStringAsync("SELECT outcome FROM result ORDER BY created_at DESC LIMIT 1;", ct);
        Assert.Equal("awaiting_review", outcome);
        Assert.Equal("succeeded", await ScalarStringAsync("SELECT status FROM queue_item;", ct));
        Assert.Equal("succeeded", await ScalarStringAsync("SELECT outcome FROM attempt;", ct));

        var queueItemId = await ScalarStringAsync("SELECT id FROM queue_item;", ct);
        var turnStart = turnLogger.Collector.GetSnapshot()
            .Single(record => record.Message.Contains("模型回合开始", StringComparison.Ordinal));
        Assert.Contains(queueItemId!, turnStart.Message, StringComparison.Ordinal);
        Assert.Contains("attempt=1", turnStart.Message, StringComparison.Ordinal);
        Assert.Contains("turnId=", turnStart.Message, StringComparison.Ordinal);

        var settled = coordinatorLogger.Collector.GetSnapshot()
            .Single(record => record.Message.Contains("队列项执行成功", StringComparison.Ordinal));
        Assert.Contains(queueItemId!, settled.Message, StringComparison.Ordinal);
        Assert.Contains("turnId=", settled.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验收 2：鉴权失败按 provider 隔离——openai 进入 auth + open，vscode 状态不受影响；
    /// 且资源类失败不消耗 attempt。
    /// </summary>
    [Fact]
    public async Task Pump_AuthFailure_BreaksOnlyOpenAiAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "会被鉴权拒绝", "ready", 10);
        await PrepareQueueAsync(ct);
        await _providerStates.SaveAsync(BreakerPolicy.OnSuccess("vscode", DateTimeOffset.UtcNow), ct);

        var fake = new FakeModelClient(
            "openai",
            (_, _, _) => Task.FromResult(TurnResult.Failed(
                new ModelError(ModelErrorKinds.Auth, "AUTH_FAILED", "鉴权失败（HTTP 401）。"))));

        var coordinator = CreateCoordinator(fake, new ExecutionOptions());
        await coordinator.PumpAsync(ct);

        // 熔断状态先落库、队列回退后落库：等到两者都完成再断言，避免读到 “auth 已生效但队列仍 running” 的中间态。
        await WaitUntilAsync(
            async () => string.Equals(
                    await ScalarStringAsync("SELECT state FROM provider_state WHERE provider = 'openai';", ct),
                    "auth",
                    StringComparison.Ordinal)
                && string.Equals(
                    await ScalarStringAsync("SELECT status FROM queue_item;", ct),
                    "pending",
                    StringComparison.Ordinal),
            ct);

        var openAi = await _providerStates.GetAsync("openai", ct);
        Assert.NotNull(openAi);
        Assert.Equal(ProviderStates.Auth, openAi!.State);
        Assert.Equal(BreakerStates.Open, openAi.Breaker);

        var vsCode = await _providerStates.GetAsync("vscode", ct);
        Assert.NotNull(vsCode);
        Assert.Equal(ProviderStates.Ready, vsCode!.State);
        Assert.Equal(BreakerStates.Closed, vsCode.Breaker);

        // 资源类失败回退 attempt，队列项仍在 pending，等 provider 恢复。
        Assert.Equal("pending", await ScalarStringAsync("SELECT status FROM queue_item;", ct));
        Assert.Equal(0L, await ScalarLongAsync("SELECT attempt FROM queue_item;", ct));
    }

    /// <summary>
    /// 验收 3：回合超过 MaxDuration 时取消，队列项置 dead、attempt 记 abandoned，
    /// 结果转 awaiting_review，且不重试（attempt 行只有 1 条）。
    /// </summary>
    [Fact]
    public async Task Pump_TurnTimeout_MarksDeadWithoutRetryAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "会超时", "ready", 10);
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return TurnResult.Done("不可达", 0, null, Array.Empty<string>());
            });

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxDurationSeconds = 1 });
        await coordinator.PumpAsync(ct);

        // 队列置 dead 后又插入失败结果，等两者都落库再断言，避免读到中间态。
        await WaitUntilAsync(
            async () => string.Equals(await ScalarStringAsync("SELECT status FROM queue_item;", ct), "dead", StringComparison.Ordinal)
                && await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1,
            ct,
            TimeSpan.FromSeconds(10));

        Assert.Equal("abandoned", await ScalarStringAsync("SELECT outcome FROM attempt;", ct));
        Assert.Equal("TURN_TIMEOUT", await ScalarStringAsync("SELECT error_code FROM attempt;", ct));
        Assert.Equal("awaiting_review", await ScalarStringAsync("SELECT outcome FROM result;", ct));
        Assert.Equal(1L, await ScalarLongAsync("SELECT COUNT(*) FROM attempt;", ct));
        Assert.Equal(1L, await ScalarLongAsync("SELECT attempt FROM queue_item;", ct));
    }

    /// <summary>
    /// 验收 5：MaxParallel=1 时两个任务严格串行；第二次 pump 才能启动第二个任务。
    /// </summary>
    [Fact]
    public async Task Pump_MaxParallelOne_SerializesTasksAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "第一个", "ready", 20);
        WriteTask(root, "T-002", "第二个", "ready", 10);
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            async (request, _, token) =>
            {
                await Task.Delay(120, token);
                return TurnResult.Done($"完成 {request.TaskId}", 0, null, Array.Empty<string>());
            });

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxParallel = 1 });

        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM queue_item WHERE status = 'succeeded';", ct) == 1, ct);
        // 队列项落终态早于并发槽位归还：等 ActiveCount 归零（此时槽位必定已释放）再发起第二次 pump，
        // 否则会偶发读到“数据库已成功但槽位仍占用”而返回 0。
        await WaitUntilAsync(() => Task.FromResult(coordinator.ActiveCount == 0), ct);
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 2, ct);

        Assert.Equal(1, fake.MaxConcurrency);
        Assert.Equal(2, fake.Calls);
    }

    /// <summary>
    /// P4 验收：停机时取消在跑回合后，队列中不残留 claimed/running 租约，队列项进入 cancelled 终态。
    /// </summary>
    [Fact]
    public async Task Shutdown_WhileRunning_LeavesNoActiveLeaseAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "长跑任务", "ready", 10);
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            async (_, _, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return TurnResult.Done("不应到达", 0, null, Array.Empty<string>());
            });

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxParallel = 1 });
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(
            async () => await CountAsync("SELECT COUNT(*) FROM queue_item WHERE status = 'running';", ct) == 1,
            ct);

        await coordinator.ShutdownAsync(ct);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM queue_item WHERE status IN ('claimed','running');", ct));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM queue_item WHERE status = 'cancelled';", ct));
    }

    /// <summary>构建执行协调器。</summary>
    /// <param name="fake">假模型客户端。</param>
    /// <param name="options">执行配置。</param>
    /// <param name="turnLogger">回合执行器日志器。</param>
    /// <param name="coordinatorLogger">协调器日志器。</param>
    private ExecutionCoordinator CreateCoordinator(
        FakeModelClient fake,
        ExecutionOptions options,
        ILogger<TurnRunner>? turnLogger = null,
        ILogger<ExecutionCoordinator>? coordinatorLogger = null)
    {
        var normalized = options.Normalize();
        var runner = new TurnRunner(
            new ModelClientRegistry(new[] { fake }),
            normalized,
            new DevHubPaths(_directory),
            NullLoggerFactory.Instance,
            turnLogger ?? NullLogger<TurnRunner>.Instance);

        return new ExecutionCoordinator(
            _queue,
            _store,
            _providerStates,
            _registry,
            runner,
            normalized,
            coordinatorLogger ?? NullLogger<ExecutionCoordinator>.Instance);
    }

    /// <summary>扫描工作区并入队全部 ready 任务。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task PrepareQueueAsync(CancellationToken cancellationToken)
    {
        var loaded = await _registry.LoadAsync(cancellationToken);
        foreach (var workspace in loaded.Workspaces)
        {
            await _scanner.ScanAsync(workspace, cancellationToken);
            await _queue.EnqueueReadyTasksAsync(workspace.Id, "openai", dryRun: false, cancellationToken);
        }
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
    /// <param name="entries">工作区条目。</param>
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

    /// <summary>轮询等待条件成立。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeout">超时；默认 5 秒。</param>
    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        Assert.Fail("等待条件超时。");
    }

    /// <summary>统计行数。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<long> CountAsync(string sql, CancellationToken cancellationToken) =>
        await ScalarLongAsync(sql, cancellationToken);

    /// <summary>读取整数值。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<long> ScalarLongAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>读取字符串值。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<string?> ScalarStringAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
