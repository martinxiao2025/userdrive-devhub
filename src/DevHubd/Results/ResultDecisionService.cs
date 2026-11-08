using DevHubd.Core.Results;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using CoreDecisionStatus = DevHubd.Core.Results.ResultDecisionStatus;
using DecisionStatus = DevHubd.Mcp.Abstractions.ResultDecisionStatus;

namespace DevHubd.Results;

/// <summary>
/// <see cref="IResultDecisionSource"/> 的宿主实现：把 Core 层审批结果映射为 camelCase 契约，
/// 工具层不直接依赖 <c>DevHubd.Core</c>。
/// </summary>
public sealed class ResultDecisionService : IResultDecisionSource
{
    private readonly ResultDecisionStore _store;

    /// <summary>注入审批存取。</summary>
    /// <param name="store">结果审批存取。</param>
    public ResultDecisionService(ResultDecisionStore store)
    {
        _store = store;
    }

    /// <summary>通过或驳回一条待审批结果。</summary>
    /// <param name="resultId">结果 id。</param>
    /// <param name="approve">true 通过，false 驳回。</param>
    /// <param name="comment">审批意见。</param>
    /// <param name="actor">操作者标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<(DecisionStatus Status, ResultDecisionResult? Result, string? Reason)> DecideAsync(
        string resultId,
        bool approve,
        string? comment,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _store
            .DecideAsync(resultId, approve, comment, actor, cancellationToken)
            .ConfigureAwait(false);

        var status = outcome.Status switch
        {
            CoreDecisionStatus.Decided => DecisionStatus.Decided,
            CoreDecisionStatus.Duplicate => DecisionStatus.Duplicate,
            CoreDecisionStatus.NotPending => DecisionStatus.NotPending,
            _ => DecisionStatus.NotFound,
        };

        var result = outcome.TaskId is null || outcome.Outcome is null
            ? null
            : new ResultDecisionResult(outcome.ResultId, outcome.Outcome, outcome.TaskId, outcome.DecidedAt);

        return (status, result, outcome.Reason);
    }
}
