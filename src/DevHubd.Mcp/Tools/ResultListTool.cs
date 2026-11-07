using System.ComponentModel;
using System.Text.Json;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary><c>result_list</c>：分页查看任务结果（默认只看未读）。</summary>
[McpServerToolType]
public sealed class ResultListTool
{
    private readonly IResultQuerySource _source;
    private readonly IToolDigestSource _digest;

    /// <summary>注入结果查询源与摘要源。</summary>
    /// <param name="source">结果查询源。</param>
    /// <param name="digest">摘要数据源。</param>
    public ResultListTool(IResultQuerySource source, IToolDigestSource digest)
    {
        _source = source;
        _digest = digest;
    }

    /// <summary>分页列出结果，按创建时间倒序。</summary>
    /// <param name="unreadOnly">true 时只返回未读。</param>
    /// <param name="limit">每页数量，1–100。</param>
    /// <param name="cursor">上一页返回的 nextCursor。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "result_list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("分页查看任务结果：包含 outcome、摘要、产物路径与已读状态。默认只看未读。")]
    public async Task<CallToolResult> ListAsync(
        [Description("只返回未读结果，默认 true。")] bool unreadOnly = true,
        [Description("每页数量，1–100，默认 10。")] int limit = 10,
        [Description("上一页返回的 nextCursor；首页留空。")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _source.ListAsync(unreadOnly, limit, cursor, cancellationToken).ConfigureAwait(false);
            return await ToolResults
                .SuccessAsync(JsonSerializer.Serialize(page, ToolJson.Options), _digest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
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
    }
}
