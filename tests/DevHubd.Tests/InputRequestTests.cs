using DevHubd.Core.Data;
using DevHubd.Core.Input;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 问题单持久化（U9）：同任务只保留一条 open、任务内容变更时取消旧单并终止挂起队列项。
/// 不调用模型，只验证持久化语义。
/// </summary>
public sealed class InputRequestTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteConnectionFactory _factory;
    private readonly InputRequestStore _store;

    /// <summary>准备独立临时数据库。</summary>
    public InputRequestTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        _store = new InputRequestStore(_factory);
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

    [Fact]
    public async Task CreateAsync_WhenOpenRequestExists_ReusesSameRequestAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await InsertQueueItemAsync("q1", "old-hash", ct);

        var first = await _store.CreateAsync(Draft("q1", "old-hash", "写入哪个目录？"), ct);
        var second = await _store.CreateAsync(Draft("q1", "old-hash", "换一个问题？"), ct);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM input_request WHERE status = 'open';", ct));
        Assert.Equal(
            1,
            await CountAsync("SELECT COUNT(*) FROM audit WHERE action = 'input.requested';", ct));
    }

    [Fact]
    public async Task CancelStaleAsync_WhenTaskContentChanged_CancelsRequestAndHeldItemAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await InsertQueueItemAsync("q1", "old-hash", ct);
        var created = await _store.CreateAsync(Draft("q1", "old-hash", "写入哪个目录？"), ct);
        await ExecuteAsync(
            """
            UPDATE queue_item SET status = 'pending', hold_kind = 'input', hold_ref = 'REQUEST_ID'
            WHERE id = 'q1';
            """.Replace("REQUEST_ID", created.RequestId, StringComparison.Ordinal),
            ct);

        // 任务文件内容已更新：task_view.content_hash 与队列项的 task_hash 不一致。
        await InsertTaskViewAsync("new-hash", ct);

        Assert.Equal(1, await _store.CancelStaleAsync("alpha", ct));

        Assert.Equal(
            "cancelled",
            await ScalarStringAsync($"SELECT status FROM input_request WHERE id = '{created.RequestId}';", ct));
        Assert.Equal(
            "cancelled|||TASK_CHANGED",
            await ScalarStringAsync(
                "SELECT status || '|' || COALESCE(hold_kind,'') || '|' || COALESCE(hold_ref,'') || '|' || COALESCE(last_error_code,'') " +
                "FROM queue_item WHERE id = 'q1';",
                ct));
        Assert.Equal(
            1,
            await CountAsync("SELECT COUNT(*) FROM audit WHERE action = 'input.cancelled';", ct));

        // 幂等：再扫一次没有新的可取消项。
        Assert.Equal(0, await _store.CancelStaleAsync("alpha", ct));
    }

    /// <summary>构造问题单创建参数。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="taskHash">任务哈希。</param>
    /// <param name="question">问题。</param>
    private static InputRequestDraft Draft(string queueItemId, string taskHash, string question) => new(
        "alpha",
        "T-1",
        queueItemId,
        "implementer",
        taskHash,
        question,
        null,
        """[{"id":"src","label":"src","value":null,"recommended":true}]""",
        """["writeScope"]""",
        AllowFreeText: true,
        DateTimeOffset.UtcNow.AddHours(24));

    /// <summary>插入一条 pending 队列项。</summary>
    /// <param name="id">队列项 id。</param>
    /// <param name="taskHash">任务哈希。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task InsertQueueItemAsync(string id, string taskHash, CancellationToken cancellationToken) => ExecuteAsync(
        $"""
        INSERT INTO queue_item (
            id, workspace_id, task_id, task_hash, dedupe_key, status, priority, attempt, max_attempts,
            preferred_provider, available_at, created_at, updated_at)
        VALUES (
            '{id}', 'alpha', 'T-1', '{taskHash}', 'dk-{id}', 'pending', 0, 0, 3,
            'openai', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
        """,
        cancellationToken);

    /// <summary>插入一条 task_view 行。</summary>
    /// <param name="contentHash">内容哈希。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task InsertTaskViewAsync(string contentHash, CancellationToken cancellationToken) => ExecuteAsync(
        """
        INSERT INTO task_view (
            workspace_id, id, source_path, content_hash, title, status, priority,
            write_scope, payload, seen_at, updated_at)
        VALUES (
            'alpha', 'T-1', '.devhub/tasks/T-1.json', 'CONTENT_HASH', '任务', 'ready', 0,
            '[]', '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
        """.Replace("CONTENT_HASH", contentHash, StringComparison.Ordinal),
        cancellationToken);

    /// <summary>统计行数。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<long> CountAsync(string sql, CancellationToken cancellationToken)
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

    /// <summary>执行一条非查询 SQL。</summary>
    /// <param name="sql">SQL 语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
