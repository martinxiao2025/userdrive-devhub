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
