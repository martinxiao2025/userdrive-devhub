using ModelContextProtocol.Protocol;
using DevHubd.Mcp.Abstractions;

namespace DevHubd.Mcp.Tools;

/// <summary>
/// 工具结果的统一构造：正常结果与错误信封都要在尾部落摘要（§11.4）。
/// 摘要生成失败时降级为 <see cref="ToolDigest.Fallback"/>，绝不让摘要拖垮工具本身。
/// </summary>
public static class ToolResults
{
    /// <summary>构造一个纯文本工具结果。</summary>
    /// <param name="text">JSON 文本。</param>
    public static CallToolResult Text(string text) => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
    };

    /// <summary>构造成功结果并追加摘要。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <param name="digest">摘要数据源；为 null 时不追加。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<CallToolResult> SuccessAsync(
        string text,
        IToolDigestSource? digest,
        CancellationToken cancellationToken)
    {
        var result = Text(text);
        return await DecorateAsync(result, digest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>构造错误结果并追加摘要。</summary>
    /// <param name="error">错误信封。</param>
    /// <param name="digest">摘要数据源；为 null 时不追加。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<CallToolResult> FailureAsync(
        CallToolResult error,
        IToolDigestSource? digest,
        CancellationToken cancellationToken)
    {
        return await DecorateAsync(error, digest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取摘要并追加；任何异常都降级为固定文案。</summary>
    /// <param name="result">工具结果。</param>
    /// <param name="digest">摘要数据源。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<CallToolResult> DecorateAsync(
        CallToolResult result,
        IToolDigestSource? digest,
        CancellationToken cancellationToken)
    {
        if (digest is null)
        {
            return result;
        }

        string body;
        try
        {
            body = await digest.BuildAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 摘要只是附属信息：失败降级，但不隐藏原因（用固定文案提示）。
            body = ToolDigest.Fallback;
        }

        return ToolDigest.Append(result, body);
    }
}
