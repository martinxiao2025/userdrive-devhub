namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>notice</c> 的持久化模型：面向扩展的通知。
/// <c>seq</c> 为 AUTOINCREMENT，单调递增，作为轮询游标；<c>dedupe_key</c> 唯一。
/// </summary>
public sealed class NoticeEntity
{
    /// <summary>自增序号（数据库生成）。</summary>
    public long Seq { get; set; }

    /// <summary>去重键：kind:subject:state:attempt。</summary>
    public string DedupeKey { get; set; } = string.Empty;

    /// <summary>result / failure / breaker / auth / provider。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>关联任务 id；provider 级通知可能为空。</summary>
    public string? TaskId { get; set; }

    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>正文。</summary>
    public string? Body { get; set; }

    /// <summary>创建时间（ISO-8601）。</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>确认时间；null 表示未确认。</summary>
    public string? AckedAt { get; set; }
}
