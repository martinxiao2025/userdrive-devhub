using DevHubd.Mcp.Abstractions;

namespace DevHubd.Mcp.Contracts;

/// <summary>
/// 手动派发状态到 MCP 字段值的唯一映射。
/// <para>
/// 不能用 <c>enum.ToString().ToLowerInvariant()</c>：多词枚举会得到 <c>activeexists</c> 这类
/// 与契约文档不一致的串。这里显式给出 snake_case，保证 <c>delivered_exists</c> /
/// <c>active_exists</c> / <c>pending_review</c> 与文档逐字对齐。
/// </para>
/// </summary>
public static class DispatchStatusText
{
    /// <summary>把派发状态转换为契约中的小写字符串。</summary>
    /// <param name="status">派发状态。</param>
    /// <returns>snake_case 状态串。</returns>
    public static string ToText(DispatchStatus status) => status switch
    {
        DispatchStatus.Enqueued => "enqueued",
        DispatchStatus.Deduped => "deduped",
        DispatchStatus.ActiveExists => "active_exists",
        DispatchStatus.DeliveredExists => "delivered_exists",
        DispatchStatus.PendingReview => "pending_review",
        DispatchStatus.WouldEnqueue => "would_enqueue",
        DispatchStatus.UnknownTask => "unknown_task",
        DispatchStatus.AmbiguousTask => "ambiguous_task",
        DispatchStatus.TaskNotReady => "task_not_ready",
        DispatchStatus.SchedulerPaused => "scheduler_paused",
        DispatchStatus.InvalidProvider => "invalid_provider",
        DispatchStatus.ProviderUnavailable => "provider_unavailable",
        DispatchStatus.NoModelProvider => "no_model_provider",
        _ => "unknown",
    };
}
