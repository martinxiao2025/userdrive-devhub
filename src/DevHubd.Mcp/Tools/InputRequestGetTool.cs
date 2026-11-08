using System.ComponentModel;
using System.Text.Json;
using DevHubd.Core.Models;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary><c>input_request_get</c>：读取单条问题单详情（含上下文字段与同任务其他问题单）。</summary>
[McpServerToolType]
public sealed class InputRequestGetTool
{
    private readonly IInputRequestService _service;
    private readonly IToolDigestSource _digest;

    /// <summary>注入问题单服务与摘要源。</summary>
    /// <param name="service">问题单服务。</param>
    /// <param name="digest">摘要数据源。</param>
    public InputRequestGetTool(IInputRequestService service, IToolDigestSource digest)
    {
        _service = service;
        _digest = digest;
    }

    /// <summary>读取问题单详情。</summary>
    /// <param name="id">问题单 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "input_request_get", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("读取一条问题单的完整内容：问题、上下文（JSON）、选项、缺少项与回答状态，并附同任务的其他问题单。")]
    public async Task<CallToolResult> GetAsync(
        [Description("问题单 id（由 input_request_list 返回）。")] string id,
        CancellationToken cancellationToken = default)
    {
        var detail = await _service.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    DevHubErrorCodes.UnknownInputRequest,
                    $"问题单不存在：{id}",
                    retryable: false,
                    next: "先用 input_request_list 获取存在的问题单 id。",
                    new { id }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }

        return await ToolResults
            .SuccessAsync(JsonSerializer.Serialize(detail, ToolJson.Options), _digest, cancellationToken)
            .ConfigureAwait(false);
    }
}
