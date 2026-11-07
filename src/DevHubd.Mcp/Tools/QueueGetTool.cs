using System.ComponentModel;
using System.Text.Json;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary><c>queue_get</c>：读取单个队列项详情与执行尝试。</summary>
[McpServerToolType]
public sealed class QueueGetTool
{
    private readonly IQueueQuerySource _source;
    private readonly IToolDigestSource _digest;

    /// <summary>注入队列查询源与摘要源。</summary>
    /// <param name="source">队列查询源。</param>
    /// <param name="digest">摘要数据源。</param>
    public QueueGetTool(IQueueQuerySource source, IToolDigestSource digest)
    {
        _source = source;
        _digest = digest;
    }

    /// <summary>按队列项 id 读取详情。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "queue_get", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("按 id 读取单个队列项的详情：状态、认领者、错误码与历次执行尝试。")]
    public async Task<CallToolResult> GetAsync(
        [Description("队列项 id（queue_list 返回的 id）。")] string queueItemId,
        CancellationToken cancellationToken = default)
    {
        var detail = await _source.GetAsync(queueItemId, cancellationToken).ConfigureAwait(false);

        if (detail is null)
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    code: "UNKNOWN_QUEUE_ITEM",
                    message: "队列项不存在。",
                    retryable: false,
                    next: "用 queue_list 找到正确的 id。",
                    details: new { queueItemId }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }

        return await ToolResults
            .SuccessAsync(JsonSerializer.Serialize(detail, ToolJson.Options), _digest, cancellationToken)
            .ConfigureAwait(false);
    }
}
