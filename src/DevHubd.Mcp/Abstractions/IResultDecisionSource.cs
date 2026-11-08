using DevHubd.Mcp.Contracts;

namespace DevHubd.Mcp.Abstractions;

/// <summary>审批处理结果（工具层据此映射错误码）。</summary>
public enum ResultDecisionStatus
{
    /// <summary>本次调用完成了审批。</summary>
    Decided,

    /// <summary>同向重复提交：幂等返回既有结果。</summary>
    Duplicate,

    /// <summary>结果不在待审批状态，且与目标状态不同。</summary>
    NotPending,

    /// <summary>结果不存在。</summary>
    NotFound,
}

/// <summary>
/// 结果审批闸门（<c>result_approve</c> / <c>result_reject</c>）。由宿主实现，
/// 工具层只做整形与错误封装。
/// </summary>
public interface IResultDecisionSource
{
    /// <summary>通过或驳回一条待审批结果。</summary>
    /// <param name="resultId">结果 id。</param>
    /// <param name="approve">true 通过（<c>delivered</c>），false 驳回（<c>failed</c>）。</param>
    /// <param name="comment">审批意见；写入审计，驳回时同时写入通知。</param>
    /// <param name="actor">操作者标识；null 时记为 <c>mcp</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<(ResultDecisionStatus Status, ResultDecisionResult? Result, string? Reason)> DecideAsync(
        string resultId,
        bool approve,
        string? comment,
        string? actor,
        CancellationToken cancellationToken = default);
}
