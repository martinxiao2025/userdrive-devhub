using System.ComponentModel;
using System.Text.Json;
using DevHubd.Core.Models;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary>
/// <c>result_approve</c>：通过一条待审批结果。结果终态置 <c>delivered</c>，
/// 任务 <c>approval_state</c> 置 <c>approved</c>，并写审计。
/// </summary>
[McpServerToolType]
public sealed class ResultApproveTool
{
    private readonly IResultDecisionSource _source;
    private readonly IToolDigestSource _digest;

    /// <summary>注入审批服务与摘要源。</summary>
    /// <param name="source">审批数据源。</param>
    /// <param name="digest">摘要数据源。</param>
    public ResultApproveTool(IResultDecisionSource source, IToolDigestSource digest)
    {
        _source = source;
        _digest = digest;
    }

    /// <summary>通过待审批结果。</summary>
    /// <param name="resultId">结果 id。</param>
    /// <param name="comment">审批意见（写入审计）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "result_approve", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("通过一条待审批结果（awaiting_review）：结果置 delivered，任务审批状态置 approved。同向重复提交幂等返回既有结果。")]
    public async Task<CallToolResult> ApproveAsync(
        [Description("结果 id（由 result_list 返回）。")] string resultId,
        [Description("审批意见，写入审计。")] string? comment = null,
        CancellationToken cancellationToken = default)
    {
        var (status, result, reason) = await _source
            .DecideAsync(resultId, approve: true, comment, actor: null, cancellationToken)
            .ConfigureAwait(false);

        if (status is ResultDecisionStatus.Decided or ResultDecisionStatus.Duplicate)
        {
            return await ToolResults
                .SuccessAsync(JsonSerializer.Serialize(result, ToolJson.Options), _digest, cancellationToken)
                .ConfigureAwait(false);
        }

        var code = status == ResultDecisionStatus.NotFound
            ? DevHubErrorCodes.UnknownResult
            : DevHubErrorCodes.ResultNotPending;

        return await ToolResults.FailureAsync(
            McpErrors.Envelope(
                code,
                reason ?? "结果不能通过。",
                retryable: false,
                next: "用 result_list 查看结果的当前 outcome 后再决定。",
                new { resultId }),
            _digest,
            cancellationToken).ConfigureAwait(false);
    }
}
