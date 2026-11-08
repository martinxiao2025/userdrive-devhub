using DevHubd.Mcp.Contracts;

namespace DevHubd.Mcp.Abstractions;

/// <summary>回答处理结果（工具层据此映射错误码）。</summary>
public enum InputAnswerStatus
{
    /// <summary>接受并恢复队列项。</summary>
    Answered,

    /// <summary>同答案重复提交：幂等返回首次结果。</summary>
    Duplicate,

    /// <summary>已回答但答案不同。</summary>
    Conflict,

    /// <summary>已过期 / 已取消。</summary>
    Closed,

    /// <summary>问题单不存在。</summary>
    NotFound,

    /// <summary>答案或选项不合法。</summary>
    InvalidAnswer,
}

/// <summary>撤销处理结果。</summary>
public enum InputCancelStatus
{
    /// <summary>撤销成功。</summary>
    Cancelled,

    /// <summary>已是终态。</summary>
    Closed,

    /// <summary>问题单不存在。</summary>
    NotFound,
}

/// <summary>不确定项问题单的会话侧操作。由宿主实现，工具层只做整形与错误封装。</summary>
public interface IInputRequestService
{
    /// <summary>分页列出问题单。</summary>
    /// <param name="workspaceId">按工作区过滤；null 表示不限。</param>
    /// <param name="taskId">按任务过滤；null 表示不限。</param>
    /// <param name="status">open / answered / expired / cancelled / all。</param>
    /// <param name="limit">1–100。</param>
    /// <param name="cursor">上一页的 nextCursor。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<InputRequestPageResult> ListAsync(
        string? workspaceId,
        string? taskId,
        string status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);

    /// <summary>读取问题单详情；不存在返回 null。</summary>
    /// <param name="id">问题单 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<InputRequestDetailResult?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>回答问题并恢复被挂起的任务。</summary>
    /// <param name="id">问题单 id。</param>
    /// <param name="answer">回答文本。</param>
    /// <param name="optionId">选中的选项 id。</param>
    /// <param name="answeredBy">回答者标识。</param>
    /// <param name="idempotencyKey">调用方幂等键（写入审计，便于追踪重试）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<(InputAnswerStatus Status, InputAnswerResult? Result, string? Reason)> AnswerAsync(
        string id,
        string answer,
        string? optionId,
        string? answeredBy,
        string? idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>撤销问题单；不存在返回 NotFound。</summary>
    /// <param name="id">问题单 id。</param>
    /// <param name="reason">撤销原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<(InputCancelStatus Status, InputCancelResult? Result, string? Reason)> CancelAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken = default);
}
