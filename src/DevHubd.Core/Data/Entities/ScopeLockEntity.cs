namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>scope_lock</c> 的持久化模型：工作区写入范围租约。
/// 当前数据访问层尚未读写该表，映射用于保持 schema 与模型一致。
/// </summary>
public sealed class ScopeLockEntity
{
    /// <summary>工作区 id（联合主键之一）。</summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>写入范围名（联合主键之一）。</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>持有者 id。</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>获取时间（ISO-8601）。</summary>
    public string AcquiredAt { get; set; } = string.Empty;

    /// <summary>到期时间（ISO-8601）。</summary>
    public string ExpiresAt { get; set; } = string.Empty;
}
