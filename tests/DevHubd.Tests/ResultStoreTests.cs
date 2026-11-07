using DevHubd.Core.Data;
using DevHubd.Core.Results;
using Xunit;

namespace DevHubd.Tests;

/// <summary>结果读侧：分页游标、未读过滤、ack 与未读聚合。</summary>
public sealed class ResultStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "devhubd-tests",
        Guid.NewGuid().ToString("N"));

    private SqliteConnectionFactory _factory = null!;

    /// <summary>准备临时数据库。</summary>
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken);
        await InsertQueueItemAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>清理临时目录。</summary>
    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>分页按 created_at 倒序，游标能取到剩余条目。</summary>
    [Fact]
    public async Task List_PagesNewestFirstWithCursorAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new ResultStore(_factory);
        await InsertResultAsync("r1", "delivered", "2026-01-01T00:00:00Z", ct);
        await InsertResultAsync("r2", "failed", "2026-01-02T00:00:00Z", ct);
        await InsertResultAsync("r3", "awaiting_review", "2026-01-03T00:00:00Z", ct);

        var first = await store.ListAsync(true, 2, null, ct);
        Assert.Equal(new[] { "r3", "r2" }, first.Items.Select(item => item.Id));
        Assert.NotNull(first.NextCursor);

        var second = await store.ListAsync(true, 2, first.NextCursor, ct);
        Assert.Equal(new[] { "r1" }, second.Items.Select(item => item.Id));
        Assert.Null(second.NextCursor);
    }

    /// <summary>ack 只标记未读，重复 ack 返回 0；ack all 清空未读。</summary>
    [Fact]
    public async Task Ack_MarksOnlyUnreadAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new ResultStore(_factory);
        await InsertResultAsync("r1", "delivered", "2026-01-01T00:00:00Z", ct);
        await InsertResultAsync("r2", "failed", "2026-01-02T00:00:00Z", ct);

        var now = "2026-02-01T00:00:00Z";
        Assert.Equal(1, await store.AckAsync(new[] { "r1" }, now, ct));
        Assert.Equal(0, await store.AckAsync(new[] { "r1" }, now, ct));

        var digest = await store.GetDigestAsync(ct);
        Assert.Equal(1, digest.Unread);
        Assert.Equal(1, digest.Failures);

        Assert.Equal(1, await store.AckAllAsync(now, ct));
        Assert.Equal(0, (await store.GetDigestAsync(ct)).Unread);
    }

    /// <summary>limit 越界直接拒绝，避免把整表拉进内存。</summary>
    [Fact]
    public async Task List_RejectsOutOfRangeLimitAsync()
    {
        var store = new ResultStore(_factory);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ListAsync(true, 0, null, TestContext.Current.CancellationToken));
    }

    /// <summary>插入一条结果（依赖已插入的队列项）。</summary>
    private async Task InsertResultAsync(string id, string outcome, string createdAt, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO result (id, task_id, queue_item_id, provider, outcome, summary, artifacts, created_at, read_at)
            VALUES ($id, 'T-001', 'q1', 'openai', $outcome, '摘要', '[]', $createdAt, NULL);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$createdAt", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>插入一条满足外键的队列项。</summary>
    private async Task InsertQueueItemAsync(CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO queue_item
                (id, workspace_id, task_id, task_hash, dedupe_key, status, priority,
                 attempt, max_attempts, preferred_provider, available_at, created_at, updated_at)
            VALUES ('q1', 'alpha', 'T-001', 'hash', 'dedupe', 'succeeded', 10,
                    1, 3, 'openai', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
