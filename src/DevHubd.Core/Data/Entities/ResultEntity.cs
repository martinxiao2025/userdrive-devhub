namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>result</c> 的持久化模型：回合产出结果。
/// <c>artifacts</c> 以 JSON 数组文本原样存储，读侧负责反序列化。
/// </summary>
public sealed class ResultEntity
{
    /// <summary>结果 id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>任务 id。</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>关联队列项 id。</summary>
    public string QueueItemId { get; set; } = string.Empty;

    /// <summary>产出该结果的 provider。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>delivered / awaiting_review / failed。</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>摘要。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>产物相对路径（JSON 数组文本）。</summary>
    public string? Artifacts { get; set; }

    /// <summary>创建时间（ISO-8601）。</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>已读时间；null 表示未读。</summary>
    public string? ReadAt { get; set; }
}
