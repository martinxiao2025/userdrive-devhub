namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>queue_item</c> 的持久化模型：待办队列项。
/// <c>dedupe_key</c> 的唯一约束是幂等入队的数据库侧保证。
/// </summary>
public sealed class QueueItemEntity
{
    /// <summary>队列项 id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>工作区 id。</summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>任务 id。</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>入队时的任务内容哈希。</summary>
    public string TaskHash { get; set; } = string.Empty;

    /// <summary>去重键：workspace:task:contentHash。</summary>
    public string DedupeKey { get; set; } = string.Empty;

    /// <summary>pending / claimed / running / succeeded / failed / dead / cancelled。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>优先级，越大越先认领。</summary>
    public int Priority { get; set; }

    /// <summary>已消耗的尝试次数。</summary>
    public int Attempt { get; set; }

    /// <summary>最大尝试次数。</summary>
    public int MaxAttempts { get; set; }

    /// <summary>首选 provider。</summary>
    public string PreferredProvider { get; set; } = string.Empty;

    /// <summary>最早可认领时间（ISO-8601 字符串）。</summary>
    public string AvailableAt { get; set; } = string.Empty;

    /// <summary>当前租约持有者；未认领为 null。</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>租约到期时间；未认领为 null。</summary>
    public string? LeaseExpiresAt { get; set; }

    /// <summary>最近一次错误码。</summary>
    public string? LastErrorCode { get; set; }

    /// <summary>最近一次错误说明。</summary>
    public string? LastError { get; set; }

    /// <summary>创建时间（ISO-8601）。</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>最后更新时间（ISO-8601）。</summary>
    public string UpdatedAt { get; set; } = string.Empty;
}
