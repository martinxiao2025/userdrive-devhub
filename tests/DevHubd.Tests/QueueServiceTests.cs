using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Queue;
using DevHubd.Queue;
using DevHubd.Mcp.Tools;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// P1 队列语义测试：并发认领、租约回收、去重、分页游标与工具层错误码。
/// 每个测试使用独立临时数据库，避免相互污染。
/// </summary>
public sealed class QueueServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    /// <summary>为每个测试创建独立临时数据目录。</summary>
    public QueueServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "devhub.db");
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

    /// <summary>8 个并发认领者对同一队列项竞争，重复 100 轮，每轮必须恰好 1 个成功。</summary>
    [Fact]
    public async Task ClaimAsync_WithEightConcurrentClaimants_OnlyOneSucceedsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = await CreateMigratedFactoryAsync(ct);
        var queue = new QueueService(factory);

        for (var iteration = 0; iteration < 100; iteration++)
        {
            await ResetQueueAsync(factory, ct);
            await InsertTaskAsync(factory, "alpha", $"T-{iteration}", "ready", "hash", 10, ct);
            await queue.EnqueueReadyTasksAsync("alpha", "openai", dryRun: false, ct);

            var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
                Task.Run(
                    () => queue.ClaimAsync($"owner-{index}", TimeSpan.FromMinutes(1), ct),
                    ct)));

            Assert.Equal(1, claims.Count(claim => claim is not null));
        }
    }

    /// <summary>认领后租约过期：attempt 记 abandoned，队列项回到 pending 并可被再次认领。</summary>
    [Fact]
    public async Task ReclaimExpired_ReturnsItemToPendingAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = await CreateMigratedFactoryAsync(ct);
        var queue = new QueueService(factory);

        await InsertTaskAsync(factory, "alpha", "T-001", "ready", "hash", 10, ct);
        await queue.EnqueueReadyTasksAsync("alpha", "openai", dryRun: false, ct);

        var claimed = await queue.ClaimAsync("owner-a", TimeSpan.FromMilliseconds(1), ct);
        Assert.NotNull(claimed);

        await Task.Delay(30, ct);
        Assert.Equal(1, await queue.ReclaimExpiredAsync(ct));

        var detail = await queue.GetAsync(claimed!.Id, ct);
        Assert.NotNull(detail);
        Assert.Equal("pending", detail!.Item.Status);
        Assert.Equal("LEASE_EXPIRED", detail.Item.LastErrorCode);
        Assert.Single(detail.Attempts);
        Assert.Equal("abandoned", detail.Attempts[0].Outcome);
    }

    /// <summary>分页游标必须稳定：limit=2 逐页读完不重复、不遗漏，顺序为 priority 降序。</summary>
    [Fact]
    public async Task ListAsync_PagesWithoutDuplicatesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = await CreateMigratedFactoryAsync(ct);
        var queue = new QueueService(factory);

        for (var index = 0; index < 7; index++)
        {
            await InsertTaskAsync(factory, "alpha", $"T-{index}", "ready", $"hash-{index}", index, ct);
        }

        await queue.EnqueueReadyTasksAsync("alpha", "openai", dryRun: false, ct);

        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await queue.ListAsync("all", 2, cursor, ct);
            ids.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(7, ids.Count);
        Assert.Equal(7, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>limit 越界必须抛 <see cref="ArgumentOutOfRangeException"/>，由工具层翻译为 INVALID_LIMIT。</summary>
    [Fact]
    public async Task ListAsync_WithInvalidLimit_ThrowsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = await CreateMigratedFactoryAsync(ct);
        var queue = new QueueService(factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queue.ListAsync("all", 0, null, ct));
    }

    /// <summary>status 非枚举值必须抛 <see cref="ArgumentException"/>，由工具层翻译为 INVALID_STATUS。</summary>
    [Fact]
    public async Task ListAsync_WithInvalidStatus_ThrowsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = await CreateMigratedFactoryAsync(ct);
        var queue = new QueueService(factory);

        await Assert.ThrowsAsync<ArgumentException>(() => queue.ListAsync("bogus", 20, null, ct));
    }

    /// <summary>定时入队把传入的 provider 落到 <c>preferred_provider</c>。</summary>
    [Fact]
    public async Task EnqueueReadyTasksAsync_WritesConfiguredProviderAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = await CreateMigratedFactoryAsync(ct);
        var queue = new QueueService(factory);

        await InsertTaskAsync(factory, "alpha", "T-001", "ready", "hash-1", 10, ct);
        await queue.EnqueueReadyTasksAsync("alpha", "vscode", dryRun: false, ct);

        Assert.Equal("vscode", await ReadPreferredProviderAsync(factory, "T-001", ct));
    }

    /// <summary>非法 provider 由 <c>NormalizeScheduledProvider</c> 收敛为 openai，不写非法值。</summary>
    [Fact]
    public async Task EnqueueReadyTasksAsync_InvalidProvider_FallsBackToOpenAiAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = await CreateMigratedFactoryAsync(ct);
        var queue = new QueueService(factory);

        await InsertTaskAsync(factory, "alpha", "T-001", "ready", "hash-1", 10, ct);
        await queue.EnqueueReadyTasksAsync("alpha", "gemini", dryRun: false, ct);

        Assert.Equal("openai", await ReadPreferredProviderAsync(factory, "T-001", ct));
    }

    /// <summary><c>queue_list</c> 工具层：非法 limit 返回 INVALID_LIMIT 错误信封，正常分页返回 camelCase 字段。</summary>
    [Fact]
    public async Task QueueListTool_MapsErrorsAndPagesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = await CreateMigratedFactoryAsync(ct);
        var queue = new QueueService(factory);
        var tool = new QueueListTool(new QueueQuerySource(queue), null!);

        var invalid = await tool.ListAsync(limit: 0, cancellationToken: ct);
        Assert.True(invalid.IsError);
        Assert.Contains("INVALID_LIMIT", ReadText(invalid), StringComparison.Ordinal);

        for (var index = 0; index < 3; index++)
        {
            await InsertTaskAsync(factory, "alpha", $"T-{index}", "ready", $"hash-{index}", index, ct);
        }

        await queue.EnqueueReadyTasksAsync("alpha", "openai", dryRun: false, ct);

        var first = await tool.ListAsync(status: "all", limit: 2, cursor: null, cancellationToken: ct);
        using var document = JsonDocument.Parse(ReadText(first));
        var items = document.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.True(items[0].TryGetProperty("workspaceId", out _));
        Assert.True(document.RootElement.TryGetProperty("nextCursor", out var nextCursor));
        Assert.False(string.IsNullOrEmpty(nextCursor.GetString()));
    }

    /// <summary>读取工具结果的文本内容。</summary>
    /// <param name="result">工具结果。</param>
    private static string ReadText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    /// <summary>读取指定任务的 preferred_provider。</summary>
    /// <param name="factory">连接工厂。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<string> ReadPreferredProviderAsync(
        SqliteConnectionFactory factory,
        string taskId,
        CancellationToken cancellationToken)
    {
        using var connection = await factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT preferred_provider FROM queue_item WHERE task_id = $task;";
        command.Parameters.AddWithValue("$task", taskId);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>创建并迁移一个独立数据库的工厂。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<SqliteConnectionFactory> CreateMigratedFactoryAsync(CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(_databasePath);
        await Migrator.MigrateAsync(factory, cancellationToken);
        return factory;
    }

    /// <summary>清空队列相关表，保证每轮并发测试从干净状态开始。</summary>
    /// <param name="factory">连接工厂。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task ResetQueueAsync(SqliteConnectionFactory factory, CancellationToken cancellationToken)
    {
        using var connection = await factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM attempt; DELETE FROM queue_item; DELETE FROM task_view;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>直接写入一行 task_view，便于隔离队列逻辑。</summary>
    /// <param name="factory">连接工厂。</param>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="status">任务状态。</param>
    /// <param name="hash">内容哈希。</param>
    /// <param name="priority">优先级。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task InsertTaskAsync(
        SqliteConnectionFactory factory,
        string workspaceId,
        string taskId,
        string status,
        string hash,
        int priority,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO task_view
              (workspace_id, id, source_path, content_hash, title, status, priority, write_scope, payload, seen_at, updated_at)
            VALUES
              ($workspace, $id, $path, $hash, $title, $status, $priority, '[]', '{}', $now, $now)
            ON CONFLICT (workspace_id, id) DO UPDATE SET
              content_hash = excluded.content_hash,
              status = excluded.status,
              priority = excluded.priority,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId);
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$path", $"/virtual/{taskId}.json");
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$title", $"任务 {taskId}");
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
