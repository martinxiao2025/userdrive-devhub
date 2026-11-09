using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using DevHubd.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Execution;

/// <summary>一次 attempt 的终态写入参数。</summary>
/// <param name="Outcome">succeeded / failed / abandoned。</param>
/// <param name="Model">实际使用的模型。</param>
/// <param name="ErrorKind">错误分类。</param>
/// <param name="ErrorCode">错误码。</param>
/// <param name="ToolCalls">工具调用次数。</param>
/// <param name="InputTokens">输入 token；provider 未返回时为 null。</param>
/// <param name="OutputTokens">输出 token；provider 未返回时为 null。</param>
/// <param name="UsageEstimated">true 表示 token 是按字符数估算的近似值，不是 provider 返回的真实 token。</param>
/// <param name="UsageAvailable">true 表示 provider 明确返回了用量；false 表示用量不可得。</param>
/// <param name="RoundsExhausted">true 表示本回合耗尽工具轮次预算，文本是移除工具后的兜底答复。</param>
/// <param name="FilesChanged">改动文件数。</param>
/// <param name="Summary">摘要。</param>
public sealed record AttemptCompletion(
    string Outcome,
    string? Model,
    string? ErrorKind,
    string? ErrorCode,
    int ToolCalls,
    int? InputTokens,
    int? OutputTokens,
    bool UsageEstimated,
    bool UsageAvailable,
    bool RoundsExhausted,
    int FilesChanged,
    string Summary);

/// <summary>写入结果表的参数。</summary>
/// <param name="TaskId">任务 id。</param>
/// <param name="QueueItemId">队列项 id。</param>
/// <param name="Provider">provider。</param>
/// <param name="Outcome">delivered / awaiting_review / failed。</param>
/// <param name="Summary">摘要。</param>
/// <param name="Artifacts">产物列表（相对路径）。</param>
public sealed record ResultDraft(
    string TaskId,
    string QueueItemId,
    string Provider,
    string Outcome,
    string Summary,
    IReadOnlyList<string> Artifacts);

/// <summary>
/// 执行结果存储：attempt 终态、队列状态迁移、result 与 notice 落库。
/// 与 <c>QueueService</c> 分开，避免队列模块同时承担模型语义。
/// 读写统一通过 EF Core（<see cref="DevHubDbContext"/>）完成；仅在 SQLite 原生 upsert
/// （<c>ON CONFLICT … DO NOTHING</c>）无法用 LINQ 表达时保留单条原生 INSERT。
/// </summary>
public sealed class ExecutionStore
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public ExecutionStore(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>用裸连接工厂构造（测试与不依赖 DI 的场景）。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public ExecutionStore(SqliteConnectionFactory factory)
        : this(new DevHubDbContextFactory(factory.ConnectionString))
    {
    }

    /// <summary>
    /// 把队列项置为 running（认领后的执行开始）。只有"状态 <c>claimed</c> + 持有者 + 本轮令牌"
    /// 三者同时匹配才迁移：否则说明认领与置 running 之间该项已被决策取消、或被回收后重新认领，
    /// 无条件写入会把已 <c>cancelled</c> 的行改回 <c>running</c> 且租约为空，永远回收不到。
    /// </summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="ownerId">执行者标识（租约持有者）。</param>
    /// <param name="leaseToken">本轮租约令牌。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示本次调用完成了 claimed → running 迁移。</returns>
    public async Task<bool> MarkRunningAsync(
        string queueItemId,
        string ownerId,
        string leaseToken,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var affected = await db.QueueItems
            .Where(item =>
                item.Id == queueItemId &&
                item.Status == "claimed" &&
                item.LeaseOwner == ownerId &&
                item.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "running")
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// 写入 attempt 终态，先写者胜：只有 <c>ended_at IS NULL</c> 的行会被更新。
    /// <para>
    /// 回收器可能已经把同一 attempt 写成 <c>abandoned + LEASE_EXPIRED</c>，随后旧回合的收尾/
    /// 旧心跳才到达；无条件整行覆盖会把 <c>ended_at</c> 和错误语义一起改写（实测已发生）。
    /// 条件更新保证已经终态的行不再被覆盖。调用方需要区分"自己写成功"与"别人已先写"时，
    /// 用返回的 bool 判断即可。
    /// </para>
    /// </summary>
    /// <param name="attemptId">attempt id。</param>
    /// <param name="completion">终态参数。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示本次调用写入了终态；false 表示 attempt 不存在或已终态。</returns>
    public async Task<bool> CompleteAttemptAsync(
        string attemptId,
        AttemptCompletion completion,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var affected = await db.Attempts
            .Where(attempt => attempt.Id == attemptId && attempt.EndedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(attempt => attempt.EndedAt, now)
                    .SetProperty(attempt => attempt.Outcome, completion.Outcome)
                    // 只有确实拿到模型名时才覆盖，等价于 COALESCE($model, model)。
                    .SetProperty(attempt => attempt.Model, attempt => completion.Model ?? attempt.Model)
                    .SetProperty(attempt => attempt.ErrorKind, completion.ErrorKind)
                    .SetProperty(attempt => attempt.ErrorCode, completion.ErrorCode)
                    .SetProperty(attempt => attempt.ToolCalls, completion.ToolCalls)
                    .SetProperty(attempt => attempt.InputTokens, completion.InputTokens)
                    .SetProperty(attempt => attempt.OutputTokens, completion.OutputTokens)
                    .SetProperty(attempt => attempt.UsageEstimated, completion.UsageEstimated)
                    .SetProperty(attempt => attempt.UsageAvailable, completion.UsageAvailable)
                    .SetProperty(attempt => attempt.RoundsExhausted, completion.RoundsExhausted)
                    .SetProperty(attempt => attempt.FilesChanged, completion.FilesChanged)
                    .SetProperty(attempt => attempt.Summary, completion.Summary),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>把队列项置为终态（succeeded / dead / cancelled）。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="status">终态。</param>
    /// <param name="errorCode">错误码；成功时为 null。</param>
    /// <param name="lastError">错误说明；成功时为 null。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SetTerminalAsync(
        string queueItemId,
        string status,
        string? errorCode,
        string? lastError,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.QueueItems
            .Where(item => item.Id == queueItemId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, status)
                    .SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                    .SetProperty(item => item.LeaseToken, string.Empty)
                    .SetProperty(item => item.LastErrorCode, errorCode)
                    .SetProperty(item => item.LastError, lastError)
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 仅当队列项仍由"状态 + 持有者 + 本轮令牌"确认属于本次回合时置为 <c>succeeded</c>。
    /// 条件更新是原子闸门：决策可能在上一次心跳成功后把该项置 <c>cancelled</c>，
    /// 或者租约已被回收并由同进程重新认领（owner 相同、令牌不同）。
    /// 返回 false 时调用方必须丢弃回合结果，不能覆盖终态或新租约。
    /// </summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="ownerId">执行者标识（租约持有者）。</param>
    /// <param name="leaseToken">本轮租约令牌。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示本次调用完成了终态迁移。</returns>
    public async Task<bool> TrySetSucceededAsync(
        string queueItemId,
        string ownerId,
        string leaseToken,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var affected = await db.QueueItems
            .Where(item =>
                item.Id == queueItemId &&
                item.LeaseOwner == ownerId &&
                item.LeaseToken == leaseToken &&
                (item.Status == "claimed" || item.Status == "running"))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "succeeded")
                    .SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                    .SetProperty(item => item.LeaseToken, string.Empty)
                    .SetProperty(item => item.LastErrorCode, (string?)null)
                    .SetProperty(item => item.LastError, (string?)null)
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// 仅当队列项仍由"状态 + 持有者 + 本轮令牌"确认属于本次回合时置为 <c>cancelled</c>。
    /// 停机收尾与心跳可能并发：心跳已经把租约续给别的回合、或回收器已清空租约时，
    /// 这里返回 false，调用方不再覆盖队列终态（attempt 的终态写入本身已幂等）。
    /// </summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="ownerId">执行者标识（租约持有者）。</param>
    /// <param name="leaseToken">本轮租约令牌。</param>
    /// <param name="errorCode">错误码（通常为 <c>SHUTDOWN</c>）。</param>
    /// <param name="lastError">错误说明。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示本次调用完成了终态迁移。</returns>
    public async Task<bool> TrySetCancelledAsync(
        string queueItemId,
        string ownerId,
        string leaseToken,
        string errorCode,
        string lastError,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var affected = await db.QueueItems
            .Where(item =>
                item.Id == queueItemId &&
                item.LeaseOwner == ownerId &&
                item.LeaseToken == leaseToken &&
                (item.Status == "claimed" || item.Status == "running"))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "cancelled")
                    .SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                    .SetProperty(item => item.LeaseToken, string.Empty)
                    .SetProperty(item => item.LastErrorCode, errorCode)
                    .SetProperty(item => item.LastError, lastError)
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>
    /// 把队列项放回 pending（可重试或资源类错误退避）。
    /// <c>attempt</c> 单调递增、不再回退：重试预算由
    /// <see cref="CountConsumingAttemptsAsync"/> 按"非 NEEDS_INPUT 回合数"口径单独判定。
    /// </summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="availableAt">下次可认领时间。</param>
    /// <param name="errorCode">错误码。</param>
    /// <param name="lastError">错误说明。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RescheduleAsync(
        string queueItemId,
        string availableAt,
        string errorCode,
        string lastError,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.QueueItems
            .Where(item => item.Id == queueItemId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "pending")
                    .SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                    .SetProperty(item => item.LeaseToken, string.Empty)
                    .SetProperty(item => item.AvailableAt, availableAt)
                    .SetProperty(item => item.LastErrorCode, errorCode)
                    .SetProperty(item => item.LastError, lastError)
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 统计该队列项已消耗重试预算的回合数：排除 <c>NEEDS_INPUT</c> 回合、
    /// <see cref="ModelErrorKinds.BudgetExempt"/>（auth / quota / no_model / bridge_down）资源类失败，
    /// 以及基础设施原因（<see cref="DevHubErrorCodes.InfrastructureAbandonCodes"/> 与
    /// <see cref="ModelErrorKinds.LeaseLost"/>）导致的 abandoned 回合。
    /// <para>
    /// 提问挂起、provider 自身不可用、租约过期/丢失、进程重启、停机都不该由任务买单：
    /// 这些回合没有把任务推进到可判定成败的状态，计进去会让一次基础设施抖动直接吃掉重试预算。
    /// 认领闸门（<c>QueueService.ClaimDetailedAsync</c>）与失败重试上限共用这一口径。
    /// </para>
    /// </summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<int> CountConsumingAttemptsAsync(
        string queueItemId,
        CancellationToken cancellationToken = default)
    {
        var exemptKinds = ModelErrorKinds.BudgetExempt;
        var exemptCodes = DevHubErrorCodes.InfrastructureAbandonCodes;
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Attempts
            .AsNoTracking()
            .CountAsync(
                attempt => attempt.QueueItemId == queueItemId &&
                           (attempt.ErrorCode == null || attempt.ErrorCode != DevHubErrorCodes.NeedsInput) &&
                           (attempt.ErrorCode == null || !exemptCodes.Contains(attempt.ErrorCode)) &&
                           (attempt.ErrorKind == null || !exemptKinds.Contains(attempt.ErrorKind)) &&
                           (attempt.ErrorKind == null || attempt.ErrorKind != ModelErrorKinds.LeaseLost),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 任务因缺少输入而挂起：队列项回到 <c>pending</c> 并写入 <c>hold_kind='input'</c>。
    /// 挂起项不占用并发槽、不参与自动重试，认领谓词的 <c>hold_kind IS NULL</c> 排除它，
    /// 直到回答清除 hold。<c>attempt</c> 不回退：每个真实回合都有独立 attempt 行，
    /// 提问回合由 <see cref="CountConsumingAttemptsAsync"/> 排除在重试预算之外。
    /// </summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="inputRequestId">问题单 id。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task HoldForInputAsync(
        string queueItemId,
        string inputRequestId,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.QueueItems
            .Where(item => item.Id == queueItemId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "pending")
                    .SetProperty(item => item.HoldKind, "input")
                    .SetProperty(item => item.HoldRef, inputRequestId)
                    .SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                    .SetProperty(item => item.LeaseToken, string.Empty)
                    .SetProperty(item => item.AvailableAt, now)
                    .SetProperty(item => item.LastErrorCode, (string?)null)
                    .SetProperty(item => item.LastError, (string?)null)
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>写入一条结果，返回结果 id。</summary>
    /// <param name="draft">结果内容。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<string> InsertResultAsync(
        ResultDraft draft,
        string now,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        db.Results.Add(new ResultEntity
        {
            Id = id,
            TaskId = draft.TaskId,
            QueueItemId = draft.QueueItemId,
            Provider = draft.Provider,
            Outcome = draft.Outcome,
            Summary = draft.Summary,
            Artifacts = JsonSerializer.Serialize(draft.Artifacts),
            CreatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// 回写任务的审批状态（<c>none/pending/approved/rejected</c>）。
    /// 只更新既有 <c>task_view</c> 行；任务文件已删除时静默跳过，不在这里创建行。
    /// </summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="state">目标审批状态。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SetApprovalStateAsync(
        string workspaceId,
        string taskId,
        string state,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.TaskViews
            .Where(task => task.WorkspaceId == workspaceId && task.Id == taskId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(task => task.ApprovalState, state)
                    .SetProperty(task => task.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>写入一条通知；dedupe_key 冲突时静默跳过（同一状态进入只提醒一次）。</summary>
    /// <param name="dedupeKey">去重键：kind:subject:state:attempt。</param>
    /// <param name="kind">result / failure / breaker / auth / provider。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="title">标题。</param>
    /// <param name="body">正文。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task InsertNoticeAsync(
        string dedupeKey,
        string kind,
        string? taskId,
        string title,
        string body,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // 去重必须由数据库判定（多进程/多连接下"先查后插"会丢幂等），
        // 而 EF 的 LINQ 没有 ON CONFLICT DO NOTHING 等价写法，故保留这一条原生 INSERT。
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO notice (dedupe_key, kind, task_id, title, body, created_at)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5})
            ON CONFLICT (dedupe_key) DO NOTHING;
            """,
            // null 值直接以 null 传递：EF 会为无类型参数生成 NULL，不需要（也不应）传 DBNull。
            new object[] { dedupeKey, kind, taskId!, title, body, now },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 进程重启兜底：把仍在执行中（<c>ended_at IS NULL</c>）的 attempt 统一标记为
    /// <c>abandoned / PROCESS_RESTART</c>。单实例互斥保证启动时不存在并发执行体，
    /// 因此这些行都属于上一个已退出进程，不能让它永远悬空。
    /// </summary>
    /// <param name="now">结束时间（UTC + Z）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被兜底关闭的 attempt 行数。</returns>
    public async Task<int> AbandonOrphanAttemptsAsync(string now, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE attempt
            SET ended_at = {0}, outcome = 'abandoned', error_code = {1}
            WHERE ended_at IS NULL;
            """,
            new object[] { now, DevHubErrorCodes.ProcessRestart },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>统计当前 running 数量（观测与测试用）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<int> CountRunningAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.QueueItems
            .AsNoTracking()
            .CountAsync(
                item => item.Status == "claimed" || item.Status == "running",
                cancellationToken)
            .ConfigureAwait(false);
    }
}
