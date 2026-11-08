namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>attempt</c> 的持久化模型：一次执行尝试。
/// <c>usage_estimated</c> 以 INTEGER 0/1 存储，由 EF 的布尔转换负责。
/// </summary>
public sealed class AttemptEntity
{
    /// <summary>attempt id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属队列项 id。</summary>
    public string QueueItemId { get; set; } = string.Empty;

    /// <summary>第几次尝试（从 1 开始）。</summary>
    public int AttemptNo { get; set; }

    /// <summary>provider。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>实际使用的模型。</summary>
    public string? Model { get; set; }

    /// <summary>开始时间（ISO-8601）。</summary>
    public string StartedAt { get; set; } = string.Empty;

    /// <summary>结束时间（ISO-8601）；未结束为 null。</summary>
    public string? EndedAt { get; set; }

    /// <summary>succeeded / failed / abandoned。</summary>
    public string? Outcome { get; set; }

    /// <summary>错误分类。</summary>
    public string? ErrorKind { get; set; }

    /// <summary>错误码。</summary>
    public string? ErrorCode { get; set; }

    /// <summary>工具调用次数。</summary>
    public int? ToolCalls { get; set; }

    /// <summary>输入 token；provider 未返回时为 null。</summary>
    public int? InputTokens { get; set; }

    /// <summary>输出 token；provider 未返回时为 null。</summary>
    public int? OutputTokens { get; set; }

    /// <summary>true 表示 token 为估算值（存 1），false 表示真实值（存 0）。</summary>
    public bool UsageEstimated { get; set; }

    /// <summary>改动文件数。</summary>
    public int? FilesChanged { get; set; }

    /// <summary>摘要。</summary>
    public string? Summary { get; set; }
}
