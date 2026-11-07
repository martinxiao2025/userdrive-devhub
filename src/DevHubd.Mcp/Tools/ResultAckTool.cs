using System.ComponentModel;
using System.Text.Json;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary><c>result_ack</c>：把结果标记为已读（指定 ids 或全部）。</summary>
[McpServerToolType]
public sealed class ResultAckTool
{
    private readonly IResultQuerySource _source;
    private readonly IToolDigestSource _digest;

    /// <summary>注入结果查询源与摘要源。</summary>
    /// <param name="source">结果查询源。</param>
    /// <param name="digest">摘要数据源。</param>
    public ResultAckTool(IResultQuerySource source, IToolDigestSource digest)
    {
        _source = source;
        _digest = digest;
    }

    /// <summary>标记已读。</summary>
    /// <param name="ids">要标记的结果 id；与 <paramref name="all"/> 二选一。</param>
    /// <param name="all">true 时标记全部未读。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "result_ack", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("把结果标记为已读：传 ids 标记指定项，或 all=true 标记全部未读。返回实际标记条数。")]
    public async Task<CallToolResult> AckAsync(
        [Description("要标记为已读的结果 id 列表。")] string[]? ids = null,
        [Description("true 时标记全部未读结果。")] bool all = false,
        CancellationToken cancellationToken = default)
    {
        if (!all && (ids is null || ids.Length == 0))
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    "INVALID_ARGUMENT",
                    "必须提供 ids 或 all=true。",
                    retryable: true,
                    next: "传 ids=[...] 标记指定结果，或 all=true 标记全部未读。",
                    new { ids, all }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }

        var result = all
            ? await _source.AckAllAsync(cancellationToken).ConfigureAwait(false)
            : await _source.AckAsync(ids!, cancellationToken).ConfigureAwait(false);

        return await ToolResults
            .SuccessAsync(JsonSerializer.Serialize(result, ToolJson.Options), _digest, cancellationToken)
            .ConfigureAwait(false);
    }
}
