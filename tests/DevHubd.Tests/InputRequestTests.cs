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

        var first = await _store.CreateAsync(Draft("T-1", "q1", "old-hash", "写入哪个目录？"), ct);
        var second = await _store.CreateAsync(Draft("T-1", "q1", "old-hash", "换一个问题？"), ct);

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
        var created = await _store.CreateAsync(Draft("T-1", "q1", "old-hash", "写入哪个目录？"), ct);
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

    [Fact]
    public async Task AnswerAsync_ResumesHeldItem_AndIsIdempotentAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await InsertQueueItemAsync("q1", "old-hash", ct);
        var created = await _store.CreateAsync(Draft("T-1", "q1", "old-hash", "写入哪个目录？"), ct);
        await HoldAsync(created.RequestId, ct);

        var accepted = await _store.AnswerAsync(created.RequestId, "src", "src", "tester", null, ct);
        Assert.Equal(InputAnswerStatus.Answered, accepted.Status);
        Assert.Equal("q1", accepted.ResumedQueueItemId);
        Assert.Equal(
            "pending||2026",
            await ScalarStringAsync(
                "SELECT status || '|' || COALESCE(hold_kind,'') || '|' || substr(available_at,1,4) FROM queue_item WHERE id = 'q1';",
                ct));
        Assert.Equal(
            "answered|src|user|tester",
            await ScalarStringAsync(
                $"SELECT status || '|' || answer || '|' || answer_source || '|' || answered_by FROM input_request WHERE id = '{created.RequestId}';",
                ct));

        // 相同答案幂等：返回首次结果，不重复恢复、不覆盖回答时间。
        var duplicate = await _store.AnswerAsync(created.RequestId, "src", "src", "tester", null, ct);
        Assert.Equal(InputAnswerStatus.Duplicate, duplicate.Status);
        Assert.Equal(accepted.AcceptedAt, duplicate.AcceptedAt);

        // 不同答案拒绝覆盖。
        var conflict = await _store.AnswerAsync(created.RequestId, "docs", "docs", "tester", null, ct);
        Assert.Equal(InputAnswerStatus.Conflict, conflict.Status);
        Assert.Equal(
            "src",
            await ScalarStringAsync(
                $"SELECT answer FROM input_request WHERE id = '{created.RequestId}';",
                ct));
    }

    [Fact]
    public async Task AnswerAsync_WhenOptionNotInRequest_ReturnsInvalidAnswerAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await InsertQueueItemAsync("q1", "old-hash", ct);
        var created = await _store.CreateAsync(Draft("T-1", "q1", "old-hash", "写入哪个目录？"), ct);
        await HoldAsync(created.RequestId, ct);

        var outcome = await _store.AnswerAsync(created.RequestId, "docs", "not-an-option", "tester", null, ct);

        Assert.Equal(InputAnswerStatus.InvalidAnswer, outcome.Status);
        Assert.Equal("open", await ScalarStringAsync($"SELECT status FROM input_request WHERE id = '{created.RequestId}';", ct));
    }

    [Fact]
    public async Task CancelAsync_TerminatesHeldItem_AndRejectsSecondCancelAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await InsertQueueItemAsync("q1", "old-hash", ct);
        var created = await _store.CreateAsync(Draft("T-1", "q1", "old-hash", "写入哪个目录？"), ct);
        await HoldAsync(created.RequestId, ct);

        var cancelled = await _store.CancelAsync(created.RequestId, "人工撤销", ct);
        Assert.Equal(InputCancelStatus.Cancelled, cancelled.Status);
        Assert.Equal(
            "cancelled|INPUT_REQUEST_CLOSED",
            await ScalarStringAsync(
                "SELECT status || '|' || COALESCE(last_error_code,'') FROM queue_item WHERE id = 'q1';",
                ct));

        Assert.Equal(InputCancelStatus.Closed, (await _store.CancelAsync(created.RequestId, null, ct)).Status);
        Assert.Equal(InputAnswerStatus.Closed, (await _store.AnswerAsync(created.RequestId, "src", null, null, null, ct)).Status);
    }

    [Fact]
    public async Task ExpireOverdueAsync_ExpiresRequestAndFailsHeldItemAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await InsertQueueItemAsync("q1", "old-hash", ct);
        var created = await _store.CreateAsync(Draft("T-1", "q1", "old-hash", "写入哪个目录？"), ct);
        await HoldAsync(created.RequestId, ct);

        // 把过期时间改到过去，模拟等待超时。
        await ExecuteAsync(
            $"UPDATE input_request SET expires_at = '2020-01-01T00:00:00.0000000Z' WHERE id = '{created.RequestId}';",
            ct);

        Assert.Equal(1, await _store.ExpireOverdueAsync("alpha", ct));
        Assert.Equal(
            "expired",
            await ScalarStringAsync($"SELECT status FROM input_request WHERE id = '{created.RequestId}';", ct));
        Assert.Equal(
            "failed|INPUT_TIMEOUT",
            await ScalarStringAsync(
                "SELECT status || '|' || COALESCE(last_error_code,'') FROM queue_item WHERE id = 'q1';",
                ct));
        Assert.Equal(
            1,
            await CountAsync("SELECT COUNT(*) FROM notice WHERE title LIKE '需要输入超时%';", ct));

        // 回答一个已过期的问题单同样被拒绝（且状态保持 expired）。
        Assert.Equal(InputAnswerStatus.Closed, (await _store.AnswerAsync(created.RequestId, "src", null, null, null, ct)).Status);
        Assert.Equal(0, await _store.ExpireOverdueAsync("alpha", ct));
    }

    [Fact]
    public async Task ListAsync_FiltersByStatusAndPaginatesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var index = 0; index < 3; index++)
        {
            await InsertQueueItemAsync($"q{index}", $"hash-{index}", ct, $"T-{index}");
            await _store.CreateAsync(
                Draft($"T-{index}", $"q{index}", $"hash-{index}", $"问题 {index}"),
                ct);
        }

        var first = await _store.ListAsync(null, null, "open", 2, null, ct);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        Assert.All(first.Items, item => Assert.Equal("open", item.Status));
        Assert.Equal(2, first.Items[0].OptionCount);

        var second = await _store.ListAsync(null, null, "open", 2, first.NextCursor, ct);
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
        Assert.Equal(
            3,
            first.Items.Concat(second.Items).Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count());

        Assert.Empty((await _store.ListAsync(null, null, "answered", 10, null, ct)).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _store.ListAsync(null, null, "bogus", 10, null, ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.ListAsync(null, null, "open", 10, "not-a-cursor", ct));

        var detail = await _store.GetAsync(first.Items[0].Id, ct);
        Assert.NotNull(detail);
        Assert.True(detail!.AllowFreeText);
        Assert.Equal(2, detail.Options.Count);
        Assert.Equal("writeScope", Assert.Single(detail.MissingInputs));
    }

    /// <summary>让问题单对应的队列项进入挂起状态。</summary>
    /// <param name="requestId">问题单 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task HoldAsync(string requestId, CancellationToken cancellationToken) => ExecuteAsync(
        $"""
        UPDATE queue_item SET status = 'pending', hold_kind = 'input', hold_ref = '{requestId}'
        WHERE id = 'q1';
        """,
        cancellationToken);

    /// <summary>构造问题单创建参数。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="taskHash">任务哈希。</param>
    /// <param name="question">问题。</param>
    private static InputRequestDraft Draft(string taskId, string queueItemId, string taskHash, string question) => new(
        "alpha",
        taskId,
        queueItemId,
        "implementer",
        taskHash,
        question,
        null,
        """[{"id":"src","label":"src","value":null,"recommended":true},{"id":"docs","label":"docs","value":null,"recommended":false}]""",
        """["writeScope"]""",
        AllowFreeText: true,
        DateTimeOffset.UtcNow.AddHours(24));

    /// <summary>插入一条 pending 队列项。</summary>
    /// <param name="id">队列项 id。</param>
    /// <param name="taskHash">任务哈希。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="taskId">任务 id。</param>
    private Task InsertQueueItemAsync(
        string id,
        string taskHash,
        CancellationToken cancellationToken,
        string taskId = "T-1") => ExecuteAsync(
        $"""
        INSERT INTO queue_item (
            id, workspace_id, task_id, task_hash, dedupe_key, status, priority, attempt, max_attempts,
            preferred_provider, available_at, created_at, updated_at)
        VALUES (
            '{id}', 'alpha', '{taskId}', '{taskHash}', 'dk-{id}', 'pending', 0, 0, 3,
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
