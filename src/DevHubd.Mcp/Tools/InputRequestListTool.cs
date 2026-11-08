using System.ComponentModel;
using System.Text.Json;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary><c>input_request_list</c>：分页查看后台任务提出的待回答 / 已回答 / 过期问题单。</summary>
[McpServerToolType]
public sealed class InputRequestListTool
{
    private readonly IInputRequestService _service;
    private readonly IToolDigestSource _digest;

    /// <summary>注入问题单服务与摘要源。</summary>
    /// <param name="service">问题单服务。</param>
    /// <param name="digest">摘要数据源。</param>
    public InputRequestListTool(IInputRequestService service, IToolDigestSource digest)
    {
        _service = service;
        _digest = digest;
    }

    /// <summary>分页列出问题单，按创建时间倒序。</summary>
    /// <param name="workspaceId">按工作区过滤；null 表示不限。</param>
    /// <param name="taskId">按任务过滤；null 表示不限。</param>
    /// <param name="status">状态过滤，默认只看 open。</param>
    /// <param name="limit">每页数量，1–100。</param>
    /// <param name="cursor">上一页返回的 nextCursor。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "input_request_list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("分页查看待回答问题单：后台任务缺少必要信息时会挂起并生成问题单。默认只看 open。")]
    public async Task<CallToolResult> ListAsync(
        [Description("按工作区 id 过滤；留空表示不限。")] string? workspaceId = null,
        [Description("按任务 id 过滤；留空表示不限。")] string? taskId = null,
        [Description("状态过滤：open/answered/expired/cancelled/all，默认 open。")] string status = "open",
        [Description("每页数量，1–100，默认 20。")] int limit = 20,
        [Description("上一页返回的 nextCursor；首页留空。")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _service
                .ListAsync(workspaceId, taskId, status, limit, cursor, cancellationToken)
                .ConfigureAwait(false);

            return await ToolResults
                .SuccessAsync(JsonSerializer.Serialize(page, ToolJson.Options), _digest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException exception) when (string.Equals(exception.ParamName, "limit", StringComparison.Ordinal))
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    "INVALID_LIMIT",
                    "limit 必须在 1–100 之间。",
                    retryable: true,
                    next: "把 limit 调整到 1–100 后重试。",
                    new { limit }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    "INVALID_STATUS",
                    $"status 不在允许范围内：{status}",
                    retryable: true,
                    next: $"使用 {ToolVocabulary.InputStatuses} 之一。",
                    new { status }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    "INVALID_CURSOR",
                    "cursor 非法：请使用上一次返回的 nextCursor。",
                    retryable: true,
                    next: "不传 cursor 重新从首页拉取。",
                    new { cursor }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
