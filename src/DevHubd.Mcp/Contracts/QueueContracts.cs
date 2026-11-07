namespace DevHubd.Mcp.Contracts;

/// <summary>队列项摘要（工具返回用，camelCase）。</summary>
public sealed record QueueItemSummary(
    string Id,
    string WorkspaceId,
    string TaskId,
    string Status,
    int Priority,
    int Attempt,
    int MaxAttempts,
    string? LeaseOwner,
    string? LeaseExpiresAt,
    string? LastErrorCode,
    string CreatedAt,
    string UpdatedAt);

/// <summary>队列分页结果。</summary>
public sealed record QueuePageResult(IReadOnlyList<QueueItemSummary> Items, string? NextCursor);

/// <summary>一次执行尝试的摘要。</summary>
public sealed record AttemptSummary(
    int AttemptNo,
    string Provider,
    string? Model,
    string StartedAt,
    string? EndedAt,
    string? Outcome,
    string? ErrorCode);

/// <summary>队列项详情。</summary>
public sealed record QueueItemDetailResult(
    QueueItemSummary Item,
    string TaskTitle,
    IReadOnlyList<AttemptSummary> Attempts);

/// <summary>手动派发结果。</summary>
public sealed record DispatchNowResult(
    string TaskId,
    string WorkspaceId,
    string Status,
    string? QueueItemId,
    string? Provider,
    bool DryRun);

/// <summary>调度控制动作后的状态快照。</summary>
public sealed record SchedulerControlSnapshot
{
    /// <summary>调度是否启用。</summary>
    public bool Enabled { get; init; }

    /// <summary>交付档位：auto / dispatch_only。</summary>
    public string Mode { get; init; } = "dispatch_only";

    /// <summary>暂停原因。</summary>
    public string? PauseReason { get; init; }

    /// <summary>扫描间隔秒数。</summary>
    public int IntervalSeconds { get; init; }

    /// <summary>本次动作是否请求了立即扫描。</summary>
    public bool ScanRequested { get; init; }

    /// <summary>本次动作是否实际改变了状态（幂等动作可为 false）。</summary>
    public bool Changed { get; init; }

    /// <summary>provider 状态（控制面动作后回显，便于确认熔断是否已重置）。</summary>
    public IReadOnlyList<ProviderStatus> Providers { get; init; } = Array.Empty<ProviderStatus>();
}

