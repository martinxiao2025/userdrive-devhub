using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using DevHubd.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Results;

/// <summary>审批决定的状态。</summary>
public enum ResultDecisionStatus
{
    /// <summary>本次调用完成了审批。</summary>
    Decided,

    /// <summary>结果已处于目标状态：幂等返回既有结果。</summary>
    Duplicate,

    /// <summary>结果不在 <c>awaiting_review</c>，且与目标状态不同：拒绝改变。</summary>
    NotPending,

    /// <summary>结果不存在。</summary>
    NotFound,
}

/// <summary>一次审批决定的持久化结果。</summary>
/// <param name="Status">决定状态。</param>
/// <param name="ResultId">结果 id。</param>
/// <param name="TaskId">任务 id；不存在时为 null。</param>
/// <param name="Outcome">结果当前（或既有）终态。</param>
/// <param name="DecidedAt">本次决定时间；<see cref="ResultDecisionStatus.Duplicate"/> 与失败时为 null。</param>
/// <param name="Reason">不可决定时的可诊断说明。</param>
public sealed record ResultDecisionOutcome(
    ResultDecisionStatus Status,
    string ResultId,
    string? TaskId,
    string? Outcome,
    string? DecidedAt,
    string? Reason);

/// <summary>
/// 结果审批闸门（U11）：把 <c>awaiting_review</c> 的结果推进到 <c>delivered</c>（通过）或
/// <c>failed</c>（驳回），并同步 <c>task_view.approval_state</c>、写审计与驳回通知。
/// 审批不改任务文件：任务清单仍由工作区维护，避免调度器改写用户文件。
/// </summary>
public sealed class ResultDecisionStore
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public ResultDecisionStore(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>用裸连接工厂构造（测试与不依赖 DI 的场景）。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public ResultDecisionStore(SqliteConnectionFactory factory)
        : this(new DevHubDbContextFactory(factory.ConnectionString))
    {
    }

    /// <summary>
    /// 通过或驳回一条待审批结果。
    /// </summary>
    /// <param name="resultId">结果 id。</param>
    /// <param name="approve">true 表示通过（<c>delivered</c>），false 表示驳回（<c>failed</c>）。</param>
    /// <param name="comment">审批意见（写入审计与驳回通知）。</param>
    /// <param name="actor">操作者标识；null 时记 <c>mcp</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ResultDecisionOutcome> DecideAsync(
        string resultId,
        bool approve,
        string? comment,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        var target = approve ? "delivered" : "failed";
        var now = UtcTime.Now();

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var entity = await db.Results
            .FirstOrDefaultAsync(result => result.Id == resultId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return new ResultDecisionOutcome(ResultDecisionStatus.NotFound, resultId, null, null, null, "结果不存在。");
        }

        // 同向重复提交是幂等的：返回既有终态，不重复写审计与通知。
        if (string.Equals(entity.Outcome, target, StringComparison.Ordinal))
        {
            return new ResultDecisionOutcome(
                ResultDecisionStatus.Duplicate,
                entity.Id,
                entity.TaskId,
                entity.Outcome,
                null,
                "结果已处于该状态，幂等返回。");
        }

        if (!string.Equals(entity.Outcome, "awaiting_review", StringComparison.Ordinal))
        {
            return new ResultDecisionOutcome(
                ResultDecisionStatus.NotPending,
                entity.Id,
                entity.TaskId,
                entity.Outcome,
                null,
                $"结果状态为 {entity.Outcome}，只有 awaiting_review 可以审批。");
        }

        entity.Outcome = target;

        var workspaceId = await db.QueueItems
            .AsNoTracking()
            .Where(item => item.Id == entity.QueueItemId)
            .Select(item => item.WorkspaceId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(workspaceId))
        {
            var approvalState = approve ? "approved" : "rejected";
            await db.TaskViews
                .Where(task => task.WorkspaceId == workspaceId && task.Id == entity.TaskId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(task => task.ApprovalState, approvalState)
                        .SetProperty(task => task.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        db.Audits.Add(new AuditEntity
        {
            At = now,
            Actor = string.IsNullOrWhiteSpace(actor) ? "mcp" : actor.Trim(),
            Action = approve ? "result.approved" : "result.rejected",
            Subject = entity.Id,
            Provider = entity.Provider,
            Detail = $"outcome={target} comment={Truncate(comment, 200)}",
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (!approve)
        {
            // 驳回必须让会话侧看到：去重键按结果 id 固定，重复驳回已在上面的 Duplicate 分支短路。
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO notice (dedupe_key, kind, task_id, title, body, created_at)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5})
                ON CONFLICT (dedupe_key) DO NOTHING;
                """,
                new object[]
                {
                    $"result-rejected:{entity.Id}",
                    "failure",
                    entity.TaskId,
                    $"任务 {entity.TaskId} 的结果被驳回",
                    string.IsNullOrWhiteSpace(comment) ? Truncate(entity.Summary, 200) : Truncate(comment, 500),
                    now,
                },
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ResultDecisionOutcome(
            ResultDecisionStatus.Decided,
            entity.Id,
            entity.TaskId,
            target,
            now,
            null);
    }

    /// <summary>按字符截断，避免审计与通知正文无限增长。</summary>
    /// <param name="value">原始文本。</param>
    /// <param name="maxLength">最大长度。</param>
    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…";
    }
}
