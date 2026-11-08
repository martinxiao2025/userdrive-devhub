namespace DevHubd.Mcp.Contracts;

/// <summary>
/// MCP 对外词表：状态、档位、动作、provider、熔断与结果终态的唯一常量来源。
/// 工具描述、错误提示与测试快照都从这里取值，避免文档与实现各写一套。
/// </summary>
public static class ToolVocabulary
{
    /// <summary>队列项状态（含过滤用的 <c>all</c>）。</summary>
    public const string QueueStatuses = "pending/claimed/running/succeeded/failed/dead/cancelled/all";

    /// <summary>交付档位。</summary>
    public const string DeliveryModes = "auto/dispatch_only";

    /// <summary>调度控制动作。</summary>
    public const string SchedulerActions = "start/pause/resume/scan_now/set_mode/reset_breaker/probe_models/status";

    /// <summary>手动派发可用的 provider 取值。</summary>
    public const string Providers = "auto/openai/vscode";

    /// <summary>provider 熔断状态。</summary>
    public const string BreakerStates = "closed/open/half_open";

    /// <summary>结果终态。</summary>
    public const string ResultOutcomes = "delivered/awaiting_review/failed";

    /// <summary>任务物化视图状态。</summary>
    public const string TaskStatuses = "ready/blocked/done/cancelled/missing";

    /// <summary>任务文件里的交付档位（与全局档位词表不同名，见 §task.schema.json）。</summary>
    public const string TaskDeliveries = "review/auto";

    /// <summary>问题单状态（含过滤用的 <c>all</c>）。</summary>
    public const string InputStatuses = "open/answered/expired/cancelled/all";
}
