using System.ComponentModel;
using System.Text.Json;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary><c>queue_list</c>：分页列出队列项。</summary>
[McpServerToolType]
public sealed class QueueListTool
{
    private readonly IQueueQuerySource _source;
    private readonly IToolDigestSource _digest;

    /// <summary>注入队列查询源与摘要源。</summary>
    /// <param name="source">队列查询源。</param>
    /// <param name="digest">摘要数据源。</param>
    public QueueListTool(IQueueQuerySource source, IToolDigestSource digest)
    {
        _source = source;
        _digest = digest;
    }

    /// <summary>按状态分页查询队列项。</summary>
    /// <param name="status">状态过滤，默认 all。</param>
    /// <param name="limit">每页数量，1–100。</param>
    /// <param name="cursor">上一页返回的 nextCursor。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "queue_list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("分页列出调度队列项；支持按状态过滤（" + ToolVocabulary.QueueStatuses + "）。")]
    public async Task<CallToolResult> ListAsync(
        [Description("状态过滤，默认 all。")] string status = "all",
        [Description("每页数量，1–100，默认 20。")] int limit = 20,
        [Description("上一页返回的 nextCursor；首页留空。")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _source.ListAsync(status, limit, cursor, cancellationToken).ConfigureAwait(false);
            return await ToolResults
                .SuccessAsync(JsonSerializer.Serialize(page, ToolJson.Options), _digest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    code: "INVALID_LIMIT",
                    message: "limit 必须在 1–100 之间。",
                    retryable: true,
                    next: "把 limit 调整到 1–100 后重试。",
                    details: new { limit }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    code: "INVALID_STATUS",
                    message: "status 取值非法。",
                    retryable: true,
                    next: "使用 " + ToolVocabulary.QueueStatuses + " 之一。",
                    details: new { status }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
