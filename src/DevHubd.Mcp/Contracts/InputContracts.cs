namespace DevHubd.Mcp.Contracts;

/// <summary>问题单摘要（<c>input_request_list</c> 返回项）。</summary>
/// <param name="Id">问题单 id。</param>
/// <param name="WorkspaceId">工作区 id。</param>
/// <param name="TaskId">任务 id。</param>
/// <param name="QueueItemId">提问的队列项 id。</param>
/// <param name="RoleId">提问时的角色 id。</param>
/// <param name="Status">open / answered / expired / cancelled。</param>
/// <param name="Question">问题文本。</param>
/// <param name="MissingInputs">缺少的输入项。</param>
/// <param name="OptionCount">选项数量。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="ExpiresAt">过期时间。</param>
/// <param name="AnsweredAt">回答时间。</param>
public sealed record InputRequestSummary(
    string Id,
    string WorkspaceId,
    string TaskId,
    string QueueItemId,
    string RoleId,
    string Status,
    string Question,
    IReadOnlyList<string> MissingInputs,
    int OptionCount,
    string CreatedAt,
    string ExpiresAt,
    string? AnsweredAt);

/// <summary>问题单分页。</summary>
/// <param name="Items">当前页。</param>
/// <param name="NextCursor">下一页游标；null 表示到底。</param>
public sealed record InputRequestPageResult(IReadOnlyList<InputRequestSummary> Items, string? NextCursor);

/// <summary>问题单选项。</summary>
/// <param name="Id">选项 id。</param>
/// <param name="Label">标签。</param>
/// <param name="Value">机器值。</param>
/// <param name="Recommended">是否推荐。</param>
public sealed record InputOptionResult(string Id, string Label, string? Value, bool Recommended);

/// <summary>同任务的其他问题单。</summary>
/// <param name="Id">问题单 id。</param>
/// <param name="Status">状态。</param>
/// <param name="Question">问题文本。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record InputRelatedResult(string Id, string Status, string Question, string CreatedAt);

/// <summary>问题单详情（<c>input_request_get</c> 返回）。</summary>
/// <param name="Id">问题单 id。</param>
/// <param name="WorkspaceId">工作区 id。</param>
/// <param name="TaskId">任务 id。</param>
/// <param name="QueueItemId">提问的队列项 id。</param>
/// <param name="RoleId">提问时的角色 id。</param>
/// <param name="Status">状态。</param>
/// <param name="Question">问题文本。</param>
/// <param name="Context">上下文 JSON 文本。</param>
/// <param name="Options">选项。</param>
/// <param name="MissingInputs">缺少的输入项。</param>
/// <param name="AllowFreeText">是否允许自由文本。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="ExpiresAt">过期时间。</param>
/// <param name="AnsweredAt">回答时间。</param>
/// <param name="Answer">回答文本。</param>
/// <param name="AnswerOptionId">选中的选项 id。</param>
/// <param name="AnswerSource">回答来源。</param>
/// <param name="AnsweredBy">回答者。</param>
/// <param name="Related">同任务的其他问题单。</param>
public sealed record InputRequestDetailResult(
    string Id,
    string WorkspaceId,
    string TaskId,
    string QueueItemId,
    string RoleId,
    string Status,
    string Question,
    string? Context,
    IReadOnlyList<InputOptionResult> Options,
    IReadOnlyList<string> MissingInputs,
    bool AllowFreeText,
    string CreatedAt,
    string ExpiresAt,
    string? AnsweredAt,
    string? Answer,
    string? AnswerOptionId,
    string? AnswerSource,
    string? AnsweredBy,
    IReadOnlyList<InputRelatedResult> Related);

/// <summary>回答成功返回。</summary>
/// <param name="Id">问题单 id。</param>
/// <param name="Status">answered / duplicate。</param>
/// <param name="ResumedQueueItemId">被恢复的队列项 id；未恢复为 null。</param>
/// <param name="NextScanAt">下一次调度扫描时间（唤醒后）。</param>
/// <param name="AcceptedAt">接受时间。</param>
public sealed record InputAnswerResult(
    string Id,
    string Status,
    string? ResumedQueueItemId,
    string? NextScanAt,
    string? AcceptedAt);

/// <summary>撤销成功返回。</summary>
/// <param name="Id">问题单 id。</param>
/// <param name="Status">cancelled。</param>
/// <param name="QueueItemId">关联队列项 id。</param>
/// <param name="CancelledAt">撤销时间。</param>
public sealed record InputCancelResult(string Id, string Status, string QueueItemId, string CancelledAt);
