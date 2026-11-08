using System.ComponentModel;
using System.Text.Json;
using DevHubd.Core.Models;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary>
/// <c>input_request_cancel</c>：人工撤销问题单。撤销后挂起队列项一并终止
/// （<c>INPUT_REQUEST_CLOSED</c>），任务不会在缺少输入的情况下继续执行。
/// </summary>
[McpServerToolType]
public sealed class InputRequestCancelTool
{
    private readonly IInputRequestService _service;
    private readonly IToolDigestSource _digest;

    /// <summary>注入问题单服务与摘要源。</summary>
    /// <param name="service">问题单服务。</param>
    /// <param name="digest">摘要数据源。</param>
    public InputRequestCancelTool(IInputRequestService service, IToolDigestSource digest)
    {
        _service = service;
        _digest = digest;
    }

    /// <summary>撤销问题单。</summary>
    /// <param name="id">问题单 id。</param>
    /// <param name="reason">撤销原因（写入审计）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "input_request_cancel", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("撤销一条待回答问题单：问题单置 cancelled，挂起的任务随之终止。仅 open 状态可撤销。")]
    public async Task<CallToolResult> CancelAsync(
        [Description("问题单 id（由 input_request_list 返回）。")] string id,
        [Description("撤销原因，写入审计。")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var (status, result, message) = await _service
            .CancelAsync(id, reason, cancellationToken)
            .ConfigureAwait(false);

        if (status == InputCancelStatus.Cancelled)
        {
            return await ToolResults
                .SuccessAsync(JsonSerializer.Serialize(result, ToolJson.Options), _digest, cancellationToken)
                .ConfigureAwait(false);
        }

        var code = status == InputCancelStatus.NotFound
            ? DevHubErrorCodes.UnknownInputRequest
            : DevHubErrorCodes.InputRequestClosed;

        return await ToolResults.FailureAsync(
            McpErrors.Envelope(
                code,
                message ?? "问题单不能撤销。",
                retryable: false,
                next: "用 input_request_get 查看当前状态后再决定。",
                new { id }),
            _digest,
            cancellationToken).ConfigureAwait(false);
    }
}
