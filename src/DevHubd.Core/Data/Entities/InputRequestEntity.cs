namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>input_request</c>（schema v5）的持久化模型：后台回合提出的问题单。
/// 主通道是持久化问题单而不是 <c>elicitation</c>：后台回合无人值守，会话侧可在任意时刻取回并回答。
/// </summary>
public sealed class InputRequestEntity
{
    /// <summary>问题单 id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>工作区 id。</summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>任务 id。</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>提出问题的队列项 id。</summary>
    public string QueueItemId { get; set; } = string.Empty;

    /// <summary>提问时生效的角色 id。</summary>
    public string RoleId { get; set; } = string.Empty;

    /// <summary>open / answered / expired / cancelled。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>面向人的一句话问题。</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>上下文 JSON（摘要、相对路径、已脱敏片段；不含凭据）。</summary>
    public string? Context { get; set; }

    /// <summary>选项 JSON 数组：<c>{ id, label, value, recommended }</c>。</summary>
    public string? Options { get; set; }

    /// <summary>缺少的输入项 JSON 数组。</summary>
    public string? MissingInputs { get; set; }

    /// <summary>是否允许自由文本。</summary>
    public bool AllowFreeText { get; set; } = true;

    /// <summary>去重键：workspace/task/task_hash/question_hash。</summary>
    public string DedupeKey { get; set; } = string.Empty;

    /// <summary>创建时间（ISO-8601）。</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>过期时间（ISO-8601）。</summary>
    public string ExpiresAt { get; set; } = string.Empty;

    /// <summary>回答时间；未回答为 null。</summary>
    public string? AnsweredAt { get; set; }

    /// <summary>回答文本。</summary>
    public string? Answer { get; set; }

    /// <summary>选中的选项 id。</summary>
    public string? AnswerOptionId { get; set; }

    /// <summary>回答来源：user / agent / elicitation。</summary>
    public string? AnswerSource { get; set; }

    /// <summary>回答者标识。</summary>
    public string? AnsweredBy { get; set; }
}
