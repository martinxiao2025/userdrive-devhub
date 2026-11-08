namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>scheduler_state</c> 的持久化模型：固定 id=1 的单例行。
/// <c>counters</c> 保持 JSON 文本，不在 EF 层做结构映射。
/// </summary>
public sealed class SchedulerStateEntity
{
    /// <summary>固定为 1（主键 + CHECK 约束）。</summary>
    public int Id { get; set; }

    /// <summary>是否启用扫描与入队（INTEGER 0/1）。</summary>
    public bool Enabled { get; set; }

    /// <summary>auto / dispatch_only。</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>暂停原因。</summary>
    public string? PauseReason { get; set; }

    /// <summary>扫描间隔秒数。</summary>
    public int IntervalSeconds { get; set; }

    /// <summary>上次扫描完成时间（ISO-8601）。</summary>
    public string? LastScanAt { get; set; }

    /// <summary>下次扫描时间（ISO-8601）。</summary>
    public string? NextScanAt { get; set; }

    /// <summary>累计计数（JSON 对象文本）。</summary>
    public string Counters { get; set; } = "{}";

    /// <summary>最后更新时间（ISO-8601）。</summary>
    public string UpdatedAt { get; set; } = string.Empty;
}
