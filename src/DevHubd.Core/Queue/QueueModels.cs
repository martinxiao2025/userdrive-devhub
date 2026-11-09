namespace DevHubd.Core.Queue;

/// <summary>队列项（<c>queue_item</c> 一行的投影）。</summary>
public sealed record QueueItem(
    string Id,
    string WorkspaceId,
    string TaskId,
    string TaskHash,
    string Status,
    int Priority,
    int Attempt,
    int MaxAttempts,
    string PreferredProvider,
    string? RoleId,
    string? HoldKind,
    string? HoldRef,
    string AvailableAt,
    string? LeaseOwner,
    string? LeaseExpiresAt,
    string? LastErrorCode,
    string? LastError,
    string CreatedAt,
    string UpdatedAt);

/// <summary>一次执行尝试（<c>attempt</c> 一行的投影）。</summary>
public sealed record AttemptRecord(
    string Id,
    int AttemptNo,
    string Provider,
    string? Model,
    string StartedAt,
    string? EndedAt,
    string? Outcome,
    string? ErrorKind,
    string? ErrorCode,
    int? ToolCalls,
    int? InputTokens,
    int? OutputTokens,
    bool UsageEstimated,
    bool UsageAvailable,
    bool RoundsExhausted,
    int? FilesChanged,
    string? Summary);

/// <summary>单个队列项的详情（含任务标题与尝试列表）。</summary>
public sealed record QueueItemDetail(QueueItem Item, string TaskTitle, IReadOnlyList<AttemptRecord> Attempts);

/// <summary>分页结果。</summary>
public sealed record QueuePage(IReadOnlyList<QueueItem> Items, string? NextCursor);

/// <summary>批量入队结果。</summary>
/// <param name="Enqueued">新增队列项数。</param>
/// <param name="Deduped">命中去重或已有待审结果而跳过数。</param>
/// <param name="SkippedActive">因已有活动队列项而跳过数。</param>
/// <param name="SkippedBackoff">因同一内容最新队列项已 dead（重试预算耗尽）而退避数。</param>
public sealed record EnqueueResult(int Enqueued, int Deduped, int SkippedActive, int SkippedBackoff);

/// <summary>单任务手动入队结果。</summary>
/// <param name="Enqueued">true 表示本次确实写入了队列项。</param>
/// <param name="Deduped">true 表示命中去重或已有未结束的同类队列项。</param>
/// <param name="QueueItemId">关联的队列项 id；仅在已存在或已写入时非 null。</param>
/// <param name="Status">pending / deduped / pending_review / delivered_exists / active_exists / would_enqueue。</param>
/// <param name="Provider">该队列项真实使用的 provider；dryRun 时为本次将要使用的 provider。</param>
public sealed record ManualEnqueueResult(
    bool Enqueued,
    bool Deduped,
    string? QueueItemId,
    string Status,
    string? Provider);

/// <summary>认领结果：队列项、本次 attempt id 与本轮租约令牌（执行器续租/落库/收尾时使用）。</summary>
/// <param name="Item">队列项投影。</param>
/// <param name="AttemptId">本次 attempt id。</param>
/// <param name="LeaseToken">本轮租约令牌：owner 相同也能区分先后租约，令牌不匹配即视为过期回合。</param>
public sealed record ClaimedQueueItem(QueueItem Item, string AttemptId, string LeaseToken);

/// <summary>task_view 中任务的最小投影。</summary>
public sealed record TaskRow(string Id, string Title, string Status, string ContentHash, int Priority);

/// <summary>任务物化视图条目（只读资源 <c>devhub://tasks</c> 使用）。</summary>
/// <param name="WorkspaceId">工作区 id。</param>
/// <param name="Id">任务 id。</param>
/// <param name="Title">标题。</param>
/// <param name="Status">ready / blocked / done / cancelled / missing。</param>
/// <param name="Priority">优先级。</param>
/// <param name="RoleId">角色 id。</param>
/// <param name="SeenAt">扫描时间。</param>
/// <param name="ApprovalState">none / pending / approved / rejected（U11）。</param>
public sealed record TaskListItem(
    string WorkspaceId,
    string Id,
    string Title,
    string Status,
    int Priority,
    string RoleId,
    string SeenAt,
    string ApprovalState);

