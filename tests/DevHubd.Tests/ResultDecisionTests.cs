using DevHubd.Core.Data;
using DevHubd.Core.Results;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 审批闸门（U11）：<c>awaiting_review</c> 的结果可被通过/驳回，同步任务审批状态，
/// 写审计；同向重复提交幂等，反向提交拒绝。不调用模型，只验证持久化语义。
/// </summary>
public sealed class ResultDecisionTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteConnectionFactory _factory;
    private readonly ResultDecisionStore _store;

    /// <summary>准备独立临时数据库。</summary>
    public ResultDecisionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        _store = new ResultDecisionStore(_factory);
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
    public async Task DecideAsync_WhenApproved_SetsDeliveredAndApprovalStateAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var decision = await _store.DecideAsync("r1", approve: true, "看起来没问题", actor: null, ct);

        Assert.Equal(ResultDecisionStatus.Decided, decision.Status);
        Assert.Equal("delivered", decision.Outcome);
        Assert.Equal("T-1", decision.TaskId);
        Assert.NotNull(decision.DecidedAt);
        Assert.Equal("delivered", await ScalarStringAsync("SELECT outcome FROM result WHERE id = 'r1';", ct));
        Assert.Equal("approved", await ScalarStringAsync("SELECT approval_state FROM task_view WHERE id = 'T-1';", ct));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM audit WHERE action = 'result.approved';", ct));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM notice;", ct));
    }

    [Fact]
    public async Task DecideAsync_WhenRejected_SetsFailedAndWritesNoticeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var decision = await _store.DecideAsync("r1", approve: false, "范围不对", actor: "reviewer", ct);

        Assert.Equal(ResultDecisionStatus.Decided, decision.Status);
        Assert.Equal("failed", decision.Outcome);
        Assert.Equal("failed", await ScalarStringAsync("SELECT outcome FROM result WHERE id = 'r1';", ct));
        Assert.Equal("rejected", await ScalarStringAsync("SELECT approval_state FROM task_view WHERE id = 'T-1';", ct));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM audit WHERE action = 'result.rejected' AND actor = 'reviewer';", ct));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM notice WHERE kind = 'failure' AND task_id = 'T-1';", ct));
        Assert.Equal(
            "范围不对",
            await ScalarStringAsync("SELECT body FROM notice WHERE dedupe_key = 'result-rejected:r1';", ct));
    }

    [Fact]
    public async Task DecideAsync_WhenSameDecisionRepeated_ReturnsDuplicateAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var first = await _store.DecideAsync("r1", approve: true, null, null, ct);
        var second = await _store.DecideAsync("r1", approve: true, null, null, ct);

        Assert.Equal(ResultDecisionStatus.Decided, first.Status);
        Assert.Equal(ResultDecisionStatus.Duplicate, second.Status);
        Assert.Equal("delivered", second.Outcome);
        Assert.Null(second.DecidedAt);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM audit WHERE action = 'result.approved';", ct));
    }

    [Fact]
    public async Task DecideAsync_WhenOppositeDecision_ReturnsNotPendingAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        await _store.DecideAsync("r1", approve: true, null, null, ct);
        var rejected = await _store.DecideAsync("r1", approve: false, null, null, ct);

        Assert.Equal(ResultDecisionStatus.NotPending, rejected.Status);
        Assert.Equal("delivered", await ScalarStringAsync("SELECT outcome FROM result WHERE id = 'r1';", ct));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM audit WHERE action = 'result.rejected';", ct));
    }

    [Fact]
    public async Task DecideAsync_WhenResultMissing_ReturnsNotFoundAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        var decision = await _store.DecideAsync("missing", approve: true, null, null, ct);

        Assert.Equal(ResultDecisionStatus.NotFound, decision.Status);
        Assert.Null(decision.TaskId);
    }

    /// <summary>插入一条已完成的队列项、任务视图与待审结果。</summary>
    /// <param name="ct">取消令牌。</param>
    private async Task SeedAsync(CancellationToken ct)
    {
        await ExecuteAsync(
            """
            INSERT INTO task_view (
                workspace_id, id, source_path, content_hash, title, status, priority,
                write_scope, payload, seen_at, updated_at, role_id, depends_on, approval_state)
            VALUES (
                'alpha', 'T-1', '.devhub/tasks/T-1.json', 'hash', '任务', 'done', 0,
                '[]', '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'implementer', '[]', 'pending');

            INSERT INTO queue_item (
                id, workspace_id, task_id, task_hash, dedupe_key, status, priority, attempt, max_attempts,
                preferred_provider, available_at, created_at, updated_at)
            VALUES (
                'q1', 'alpha', 'T-1', 'hash', 'dk-q1', 'succeeded', 0, 1, 3,
                'openai', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

            INSERT INTO result (id, task_id, queue_item_id, provider, outcome, summary, artifacts, created_at)
            VALUES ('r1', 'T-1', 'q1', 'openai', 'awaiting_review', '完成', '[]', '2026-01-01T00:00:00Z');
            """,
            ct);
    }

    /// <summary>统计行数。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task<long> CountAsync(string sql, CancellationToken ct)
    {
        using var connection = await _factory.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>读取字符串值。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task<string?> ScalarStringAsync(string sql, CancellationToken ct)
    {
        using var connection = await _factory.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>执行一条非查询 SQL。</summary>
    /// <param name="sql">SQL 语句。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        using var connection = await _factory.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
