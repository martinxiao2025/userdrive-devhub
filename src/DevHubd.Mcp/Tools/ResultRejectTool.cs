using System.ComponentModel;
using System.Text.Json;
using DevHubd.Core.Models;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary>
/// <c>result_reject</c>：驳回一条待审批结果。结果终态置 <c>failed</c>，
/// 任务 <c>approval_state</c> 置 <c>rejected</c>，写审计与 failure 通知。
/// </summary>
[McpServerToolType]
public sealed class ResultRejectTool
{
    private readonly IResultDecisionSource _source;
    private readonly IToolDigestSource _digest;

    /// <summary>注入审批服务与摘要源。</summary>
    /// <param name="source">审批数据源。</param>
    /// <param name="digest">摘要数据源。</param>
    public ResultRejectTool(IResultDecisionSource source, IToolDigestSource digest)
    {
        _source = source;
        _digest = digest;
    }

    /// <summary>驳回待审批结果。</summary>
    /// <param name="resultId">结果 id。</param>
    /// <param name="comment">驳回原因（写入审计与通知）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "result_reject", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("驳回一条待审批结果（awaiting_review）：结果置 failed，任务审批状态置 rejected，并写 failure 通知。同向重复提交幂等返回既有结果。")]
    public async Task<CallToolResult> RejectAsync(
        [Description("结果 id（由 result_list 返回）。")] string resultId,
        [Description("驳回原因，写入审计与通知。")] string? comment = null,
        CancellationToken cancellationToken = default)
    {
        var (status, result, reason) = await _source
            .DecideAsync(resultId, approve: false, comment, actor: null, cancellationToken)
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
                reason ?? "结果不能驳回。",
                retryable: false,
                next: "用 result_list 查看结果的当前 outcome 后再决定。",
                new { resultId }),
            _digest,
            cancellationToken).ConfigureAwait(false);
    }
}
