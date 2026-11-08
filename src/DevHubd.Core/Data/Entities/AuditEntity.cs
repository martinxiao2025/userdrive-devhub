namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>audit</c> 的持久化模型：控制面审计流水。
/// <c>id</c> 为 AUTOINCREMENT，由数据库生成。
/// </summary>
public sealed class AuditEntity
{
    /// <summary>自增主键（数据库生成）。</summary>
    public long Id { get; set; }

    /// <summary>发生时间（ISO-8601）。</summary>
    public string At { get; set; } = string.Empty;

    /// <summary>操作者（如 mcp:claude）。</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>动作名。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>对象。</summary>
    public string? Subject { get; set; }

    /// <summary>相关 provider。</summary>
    public string? Provider { get; set; }

    /// <summary>细节。</summary>
    public string? Detail { get; set; }

    /// <summary>链路 id。</summary>
    public string? TraceId { get; set; }
}
