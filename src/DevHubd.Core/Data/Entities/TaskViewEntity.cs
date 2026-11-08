namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>task_view</c> 的持久化模型：任务文件物化视图。
/// 与领域 record 分离，避免 EF 元数据污染对外契约；映射见 <see cref="DevHubDbContext"/>。
/// </summary>
public sealed class TaskViewEntity
{
    /// <summary>工作区 id（联合主键之一）。</summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>任务 id（联合主键之一）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>任务文件绝对路径。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>任务内容哈希。</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>ready / blocked / done / cancelled / missing。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>优先级，越大越先派发。</summary>
    public int Priority { get; set; }

    /// <summary>写入范围（JSON 数组文本）。</summary>
    public string WriteScope { get; set; } = string.Empty;

    /// <summary>任务原文（JSON 文本）。</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>最近一次被扫描到的时间（ISO-8601）。</summary>
    public string SeenAt { get; set; } = string.Empty;

    /// <summary>最后更新时间（ISO-8601）。</summary>
    public string UpdatedAt { get; set; } = string.Empty;
}
