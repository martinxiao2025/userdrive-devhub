namespace DevHubd.Mcp.Contracts;

/// <summary>
/// <c>scheduler_status</c> 的返回结构，字段与设计文档 §11.1 一一对应。
/// P0 只填充骨架值，P1 起接真实队列与调度状态。
/// </summary>
public sealed record SchedulerStatusSnapshot
{
    /// <summary>调度器是否启用。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>交付档位：<c>auto</c> 或 <c>dispatch_only</c>。</summary>
    public string Mode { get; init; } = "dispatch_only";

    /// <summary>运行阶段：idle / scanning / dispatching / paused。</summary>
    public string Phase { get; init; } = "idle";

    /// <summary>暂停原因：manual / quota / auth / no_model / bridge_down，或 null。</summary>
    public string? PauseReason { get; init; }

    /// <summary>扫描间隔（秒）。</summary>
    public int IntervalSeconds { get; init; }

    /// <summary>最近一次扫描时间（UTC ISO-8601）。</summary>
    public string? LastScanAt { get; init; }

    /// <summary>下次扫描时间（UTC ISO-8601）。</summary>
    public string? NextScanAt { get; init; }

    /// <summary>累计计数器。</summary>
    public SchedulerCounters Counters { get; init; } = new();

    /// <summary>队列各状态数量。</summary>
    public QueueCounters Queue { get; init; } = new();

    /// <summary>provider 状态列表（openai / vscode）。</summary>
    public IReadOnlyList<ProviderStatus> Providers { get; init; } = Array.Empty<ProviderStatus>();

    /// <summary>MCP 端点绑定信息。</summary>
    public McpBindingStatus Mcp { get; init; } = new();

    /// <summary>宿主进程信息。</summary>
    public HostStatus Host { get; init; } = new();

    /// <summary>未读结果与失败数。</summary>
    public UnreadCounters Unread { get; init; } = new();

    /// <summary>全部 attempt 的 token 用量汇总（provider 未返回 usage 的不计入）。</summary>
    public TokenCounters Tokens { get; init; } = new();

    /// <summary>角色快照与并发占用（来自 <c>role_view</c>，见架构增量方案 §4.2）。</summary>
    public IReadOnlyList<RoleStatus> Roles { get; init; } = Array.Empty<RoleStatus>();

    /// <summary>执行边界：两个交付档位下模型是否执行、读根与写根各在哪里。</summary>
    public ExecutionBoundary Execution { get; init; } = new();
}

/// <summary>
/// 执行边界。两个档位下模型都会执行回合，区别只在写根与结果去向：
/// auto 写工作区（受 writeScope 约束）并直接交付；dispatch_only 只写隔离目录，结果待人工复核。
/// </summary>
public sealed record ExecutionBoundary
{
    /// <summary>模型是否仍执行回合。两个档位都为 true，dispatch_only 只是不改工作区。</summary>
    public bool ModelRuns { get; init; } = true;

    /// <summary>读根：固定为 <c>workspace</c>，只读，<c>read_file</c> / <c>list_dir</c> / <c>search</c> 可读源码与既有文件。</summary>
    public string ReadRoot { get; init; } = "workspace";

    /// <summary>写根：auto 为 <c>workspace</c>；dispatch_only 为 <c>isolation</c>（<c>work/&lt;queueItemId&gt;</c>）。</summary>
    public string WriteRoot { get; init; } = "isolation";

    /// <summary>结果去向：delivered / awaiting_review。</summary>
    public string ResultDelivery { get; init; } = "awaiting_review";
}

/// <summary>单个角色的声明摘要与当前占用。</summary>
public sealed record RoleStatus
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Delivery { get; init; } = "dispatch_only";
    public int MaxParallel { get; init; }
    public int Running { get; init; }
}

/// <summary>累计计数器（§11.1）。</summary>
public sealed record SchedulerCounters
{
    public int Scanned { get; init; }
    public int Enqueued { get; init; }
    public int Dispatched { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Deferred { get; init; }
    public int Reclaimed { get; init; }
    public int SkippedOverlap { get; init; }
}

/// <summary>队列各状态数量。</summary>
public sealed record QueueCounters
{
    public int Pending { get; init; }
    public int Claimed { get; init; }
    public int Running { get; init; }
    public int Dead { get; init; }
}

/// <summary>单个 provider 的健康与熔断状态。</summary>
public sealed record ProviderStatus
{
    public string Provider { get; init; } = string.Empty;
    public string State { get; init; } = "down";
    public string? Model { get; init; }
    public string Breaker { get; init; } = "closed";
    public string? Detail { get; init; }
    public string? CheckedAt { get; init; }
}

/// <summary>MCP 端点绑定状态。</summary>
public sealed record McpBindingStatus
{
    public bool Bound { get; init; }
    public int Port { get; init; }
}

/// <summary>宿主进程状态。</summary>
public sealed record HostStatus
{
    public int Pid { get; init; }
    public string EngineMode { get; init; } = "skeleton";
    public string? StartedAt { get; init; }
    public long UptimeSeconds { get; init; }
    public string Version { get; init; } = "0.1.0";
    public string DataDirectory { get; init; } = string.Empty;
    public int SchemaVersion { get; init; }
}

/// <summary>未读结果与失败数。</summary>
public sealed record UnreadCounters
{
    public int Results { get; init; }
    public int Failures { get; init; }
}

/// <summary>
/// token 用量汇总。<see cref="EstimatedAttempts"/> 不为 0 时，对应的 token 是按字符数估算的近似值，
/// <b>不是 provider 返回的真实 token</b>。
/// </summary>
public sealed record TokenCounters
{
    /// <summary>输入 token 累计。</summary>
    public int PromptTokens { get; init; }

    /// <summary>输出 token 累计。</summary>
    public int CompletionTokens { get; init; }

    /// <summary>总 token 累计（输入 + 输出）。</summary>
    public int TotalTokens { get; init; }

    /// <summary>有 usage 的 attempt 数。</summary>
    public int AttemptsWithUsage { get; init; }

    /// <summary>provider 未返回 usage 的 attempt 数。</summary>
    public int AttemptsWithoutUsage { get; init; }

    /// <summary>用量为估算值的 attempt 数。</summary>
    public int EstimatedAttempts { get; init; }
}
