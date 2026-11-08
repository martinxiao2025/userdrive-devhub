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
public sealed record EnqueueResult(int Enqueued, int Deduped, int SkippedActive);

/// <summary>单任务手动入队结果。</summary>
/// <param name="Enqueued">true 表示本次确实写入了队列项。</param>
/// <param name="Deduped">true 表示命中去重或已有未结束的同类队列项。</param>
/// <param name="QueueItemId">关联的队列项 id；仅在已存在或已写入时非 null。</param>
/// <param name="Status">pending / deduped / active_exists / would_enqueue。</param>
/// <param name="Provider">该队列项真实使用的 provider；dryRun 时为本次将要使用的 provider。</param>
public sealed record ManualEnqueueResult(
    bool Enqueued,
    bool Deduped,
    string? QueueItemId,
    string Status,
    string? Provider);

/// <summary>认领结果：队列项与本次 attempt id（执行器回写终态时使用）。</summary>
public sealed record ClaimedQueueItem(QueueItem Item, string AttemptId);

/// <summary>task_view 中任务的最小投影。</summary>
public sealed record TaskRow(string Id, string Title, string Status, string ContentHash, int Priority);

/// <summary>任务物化视图条目（只读资源 <c>devhub://tasks</c> 使用）。</summary>
public sealed record TaskListItem(
    string WorkspaceId,
    string Id,
    string Title,
    string Status,
    int Priority,
    string RoleId,
    string SeenAt);

