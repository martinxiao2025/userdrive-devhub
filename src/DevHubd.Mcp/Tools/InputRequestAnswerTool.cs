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
/// <c>input_request_answer</c>：回答问题并恢复挂起的任务。
/// 同一答案重复提交幂等返回首次结果；不同答案返回 <c>ANSWER_CONFLICT</c>，不覆盖。
/// </summary>
[McpServerToolType]
public sealed class InputRequestAnswerTool
{
    private readonly IInputRequestService _service;
    private readonly IToolDigestSource _digest;

    /// <summary>注入问题单服务与摘要源。</summary>
    /// <param name="service">问题单服务。</param>
    /// <param name="digest">摘要数据源。</param>
    public InputRequestAnswerTool(IInputRequestService service, IToolDigestSource digest)
    {
        _service = service;
        _digest = digest;
    }

    /// <summary>回答问题并触发一次调度唤醒。</summary>
    /// <param name="id">问题单 id。</param>
    /// <param name="answer">回答文本。</param>
    /// <param name="optionId">选中的选项 id；自由文本回答可留空。</param>
    /// <param name="answeredBy">回答者标识（写入审计）。</param>
    /// <param name="idempotencyKey">调用方幂等键（写入审计，便于追踪重试）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "input_request_answer", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("回答一条问题单：写入答案、清除队列项挂起并唤醒调度扫描。相同答案重复提交幂等；不同答案会被拒绝。")]
    public async Task<CallToolResult> AnswerAsync(
        [Description("问题单 id（由 input_request_list 返回）。")] string id,
        [Description("回答文本，1–4000 字符。")] string answer,
        [Description("选中的选项 id；必须属于该问题单的 options。")] string? optionId = null,
        [Description("回答者标识，写入审计；默认 mcp。")] string? answeredBy = null,
        [Description("调用方幂等键，写入审计便于追踪重试；幂等本身由相同答案保证。")] string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var (status, result, reason) = await _service
            .AnswerAsync(id, answer, optionId, answeredBy, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        switch (status)
        {
            case InputAnswerStatus.Answered:
            case InputAnswerStatus.Duplicate:
                return await ToolResults
                    .SuccessAsync(JsonSerializer.Serialize(result, ToolJson.Options), _digest, cancellationToken)
                    .ConfigureAwait(false);

            case InputAnswerStatus.NotFound:
                return await FailureAsync(DevHubErrorCodes.UnknownInputRequest, reason ?? "问题单不存在。", id, cancellationToken)
                    .ConfigureAwait(false);

            case InputAnswerStatus.Conflict:
                return await FailureAsync(DevHubErrorCodes.AnswerConflict, reason ?? "问题单已回答，答案不可覆盖。", id, cancellationToken)
                    .ConfigureAwait(false);

            case InputAnswerStatus.InvalidAnswer:
                return await FailureAsync(DevHubErrorCodes.InvalidAnswer, reason ?? "答案不合法。", id, cancellationToken)
                    .ConfigureAwait(false);

            default:
                return await FailureAsync(DevHubErrorCodes.InputRequestClosed, reason ?? "问题单已关闭。", id, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>构造回答失败的错误信封。</summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">消息。</param>
    /// <param name="id">问题单 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task<CallToolResult> FailureAsync(
        string code,
        string message,
        string id,
        CancellationToken cancellationToken) => ToolResults.FailureAsync(
        McpErrors.Envelope(
            code,
            message,
            retryable: false,
            next: "用 input_request_get 查看当前状态后再决定。",
            new { id }),
        _digest,
        cancellationToken);
}
