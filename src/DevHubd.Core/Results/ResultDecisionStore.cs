using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using DevHubd.Core.Models;
using DevHubd.Core.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>任务文件回写失败：本次审批/驳回已回滚，结果仍是 <c>awaiting_review</c>。</summary>
    FileWriteFailed,
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
/// <c>failed</c>（驳回），并同步 <c>task_view</c> 与任务文件：
/// 通过 → 任务 <c>done</c>；驳回 → 任务回到 <c>ready</c>（可重新派发）。
/// 两处同时回写，避免"审批态变了、任务状态不动"的半边同步。
/// </summary>
public sealed class ResultDecisionStore
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;
    private readonly ILogger<ResultDecisionStore> _logger;

    /// <summary>注入 EF Core 上下文工厂与日志器。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    /// <param name="logger">日志器；null 时使用 NullLogger（测试场景）。</param>
    public ResultDecisionStore(
        IDbContextFactory<DevHubDbContext> contextFactory,
        ILogger<ResultDecisionStore>? logger = null)
    {
        _contextFactory = contextFactory;
        _logger = logger ?? NullLogger<ResultDecisionStore>.Instance;
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

        var workspaceId = await db.QueueItems
            .AsNoTracking()
            .Where(item => item.Id == entity.QueueItemId)
            .Select(item => item.WorkspaceId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // 任务状态与审批态同事务回写：通过 → done，驳回 → ready（保留可重做），待审不变。
        // 关键顺序：先写任务文件，再提交数据库。文件不可写时整体回滚，绝不产生
        // "库已交付、文件未落"的中间态——否则扫描器下一轮会按文件把已交付任务退回可执行。
        var taskStatus = approve ? "done" : "ready";
        TaskViewEntity? taskView = null;
        if (!string.IsNullOrEmpty(workspaceId))
        {
            taskView = await db.TaskViews
                .FirstOrDefaultAsync(
                    task => task.WorkspaceId == workspaceId && task.Id == entity.TaskId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (taskView is not null)
            {
                var fileError = await TryWriteTaskFileAsync(taskView, taskStatus, cancellationToken).ConfigureAwait(false);
                if (fileError is not null)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new ResultDecisionOutcome(
                        ResultDecisionStatus.FileWriteFailed,
                        entity.Id,
                        entity.TaskId,
                        "awaiting_review",
                        null,
                        fileError);
                }

                taskView.ApprovalState = approve ? "approved" : "rejected";
                taskView.Status = taskStatus;
                taskView.UpdatedAt = now;
            }
        }

        entity.Outcome = target;

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

    /// <summary>
    /// 在审批事务提交前回写任务文件状态。
    /// </summary>
    /// <param name="taskView">任务视图行（提供文件路径）。</param>
    /// <param name="status">目标状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功或可跳过时返回 null；文件不可写时返回可诊断原因，由调用方回滚事务。</returns>
    private async Task<string?> TryWriteTaskFileAsync(
        TaskViewEntity taskView,
        string status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskView.SourcePath))
        {
            _logger.LogWarning(
                "任务视图缺少 source_path，跳过任务文件回写：workspaceId={WorkspaceId} taskId={TaskId}",
                taskView.WorkspaceId,
                taskView.Id);
            return null;
        }

        try
        {
            var written = await TaskFileStatusUpdater
                .TrySetStatusAsync(taskView.SourcePath, status, cancellationToken)
                .ConfigureAwait(false);

            if (written)
            {
                _logger.LogInformation(
                    "任务文件状态已回写：taskId={TaskId} status={Status} path={Path}",
                    taskView.Id,
                    status,
                    taskView.SourcePath);
                return null;
            }

            // 文件不存在或不是 JSON 对象：不阻断审批。文件缺失时扫描器会标 missing，
            // 不是可执行状态；非法 JSON 会被扫描器跳过，都不会把已交付任务退回可执行。
            _logger.LogWarning(
                "任务文件不存在或不是 JSON 对象，跳过回写：taskId={TaskId} path={Path}",
                taskView.Id,
                taskView.SourcePath);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogError(
                exception,
                "任务文件状态回写失败，审批已回滚：taskId={TaskId} status={Status} path={Path}",
                taskView.Id,
                status,
                taskView.SourcePath);
            return $"任务文件无法写入（{exception.GetType().Name}）：{taskView.SourcePath}";
        }
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
