namespace DevHubd.Core.Input;

/// <summary>问题单创建参数。</summary>
/// <param name="WorkspaceId">工作区 id。</param>
/// <param name="TaskId">任务 id。</param>
/// <param name="QueueItemId">提问的队列项 id。</param>
/// <param name="RoleId">提问时生效的角色 id。</param>
/// <param name="TaskHash">提问时的任务内容哈希，用于识别"任务已变更"。</param>
/// <param name="Question">面向人的问题。</param>
/// <param name="ContextJson">上下文 JSON；null 表示无上下文。</param>
/// <param name="OptionsJson">选项 JSON；null 表示无固定选项。</param>
/// <param name="MissingInputsJson">缺少输入项 JSON；null 表示未声明。</param>
/// <param name="AllowFreeText">是否允许自由文本。</param>
/// <param name="ExpiresAt">过期时间。</param>
public sealed record InputRequestDraft(
    string WorkspaceId,
    string TaskId,
    string QueueItemId,
    string RoleId,
    string TaskHash,
    string Question,
    string? ContextJson,
    string? OptionsJson,
    string? MissingInputsJson,
    bool AllowFreeText,
    DateTimeOffset ExpiresAt);

/// <summary>问题单创建结果。</summary>
/// <param name="RequestId">问题单 id。</param>
/// <param name="Created">true 表示本次新建；false 表示同一任务已有 open 请求。</param>
public sealed record InputRequestCreateResult(string RequestId, bool Created);
