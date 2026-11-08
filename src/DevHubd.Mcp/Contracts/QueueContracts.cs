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

/// <summary>
/// 一次执行尝试的摘要。
/// token 字段来自 provider 返回的 usage；provider 未返回时三项均为 null，且 <c>usageAvailable=false</c>。
/// <c>usageEstimated=true</c> 表示数值是按字符数估算的近似值，<b>不是真实 token</b>。
/// <c>roundsExhausted=true</c> 表示本回合耗尽工具轮次预算，最终文本是移除工具后的兜底答复，
/// 与模型自然完成不是同一语义（结果状态仍为 succeeded）。
/// </summary>
public sealed record AttemptSummary(
    int AttemptNo,
    string Provider,
    string? Model,
    string StartedAt,
    string? EndedAt,
    string? Outcome,
    string? ErrorCode,
    int? ToolCalls,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    bool UsageEstimated,
    bool UsageAvailable,
    bool RoundsExhausted);

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

    /// <summary>本次调用是否启动了后台 provider 探测（<c>probe_models</c>）；结果用 scheduler_status 轮询。</summary>
    public bool ProbeStarted { get; init; }

    /// <summary>provider 状态（控制面动作后回显，便于确认熔断是否已重置）。</summary>
    public IReadOnlyList<ProviderStatus> Providers { get; init; } = Array.Empty<ProviderStatus>();
}

