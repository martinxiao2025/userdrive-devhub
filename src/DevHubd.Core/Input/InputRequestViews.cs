namespace DevHubd.Core.Input;

/// <summary>问题单列表行（MCP <c>input_request_list</c> 的数据来源）。</summary>
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
public sealed record InputRequestRow(
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
public sealed record InputRequestPage(IReadOnlyList<InputRequestRow> Items, string? NextCursor);

/// <summary>同一任务的其他问题单（问题单详情里的 <c>related[]</c>）。</summary>
/// <param name="Id">问题单 id。</param>
/// <param name="Status">状态。</param>
/// <param name="Question">问题文本。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record InputRequestRelated(string Id, string Status, string Question, string CreatedAt);

/// <summary>问题单详情（MCP <c>input_request_get</c> 的数据来源）。</summary>
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
public sealed record InputRequestDetail(
    string Id,
    string WorkspaceId,
    string TaskId,
    string QueueItemId,
    string RoleId,
    string Status,
    string Question,
    string? Context,
    IReadOnlyList<InputOption> Options,
    IReadOnlyList<string> MissingInputs,
    bool AllowFreeText,
    string CreatedAt,
    string ExpiresAt,
    string? AnsweredAt,
    string? Answer,
    string? AnswerOptionId,
    string? AnswerSource,
    string? AnsweredBy,
    IReadOnlyList<InputRequestRelated> Related);

/// <summary>回答结果状态。</summary>
public enum InputAnswerStatus
{
    /// <summary>本次接受并恢复队列项。</summary>
    Answered,

    /// <summary>同一答案重复提交：返回首次结果，不覆盖。</summary>
    Duplicate,

    /// <summary>已回答但答案不同：不覆盖。</summary>
    Conflict,

    /// <summary>已过期 / 已取消，不能再回答。</summary>
    Closed,

    /// <summary>问题单不存在。</summary>
    NotFound,

    /// <summary>答案或选项不合法。</summary>
    InvalidAnswer,
}

/// <summary>回答结果。</summary>
/// <param name="Status">状态。</param>
/// <param name="RequestId">问题单 id。</param>
/// <param name="ResumedQueueItemId">被恢复的队列项 id；未恢复为 null。</param>
/// <param name="AcceptedAt">接受时间（幂等提交返回首次时间）。</param>
/// <param name="Reason">失败说明。</param>
public sealed record InputAnswerOutcome(
    InputAnswerStatus Status,
    string RequestId,
    string? ResumedQueueItemId,
    string? AcceptedAt,
    string? Reason);

/// <summary>撤销结果状态。</summary>
public enum InputCancelStatus
{
    /// <summary>本次撤销成功。</summary>
    Cancelled,

    /// <summary>已是终态，不能撤销。</summary>
    Closed,

    /// <summary>问题单不存在。</summary>
    NotFound,
}

/// <summary>撤销结果。</summary>
/// <param name="Status">状态。</param>
/// <param name="RequestId">问题单 id。</param>
/// <param name="QueueItemId">关联队列项 id。</param>
/// <param name="CancelledAt">撤销时间。</param>
/// <param name="Reason">失败说明。</param>
public sealed record InputCancelOutcome(
    InputCancelStatus Status,
    string RequestId,
    string QueueItemId,
    string? CancelledAt,
    string? Reason);
