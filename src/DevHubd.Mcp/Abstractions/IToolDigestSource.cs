namespace DevHubd.Mcp.Abstractions;

/// <summary>
/// 工具结果尾部摘要的数据源（§11.4）。由宿主实现，负责拼出"未读 / 队列 / 调度"三组计数；
/// 工具层只负责追加与截断，保证任何工具都不会漏掉摘要。
/// </summary>
public interface IToolDigestSource
{
    /// <summary>
    /// 生成一行摘要正文（不含分隔符与前缀），例如
    /// <c>未读 3（失败 1）· 队列 待派发 2 / 执行中 1 · 调度 paused(quota)</c>。
    /// 数据不可用时返回一句降级说明，而不是抛异常。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<string> BuildAsync(CancellationToken cancellationToken = default);
}

/// <summary>摘要正文的固定前缀。</summary>
public static class ToolDigest
{
    /// <summary>摘要总长度上限（含前缀与分隔符）。</summary>
    public const int MaxLength = 240;

    /// <summary>摘要行前缀。</summary>
    public const string Prefix = "— devhub · ";

    /// <summary>数据源不可用时的降级正文。</summary>
    public const string Fallback = "摘要不可用";

    /// <summary>
    /// 把摘要作为**独立的文本块**追加到工具结果末尾。
    /// 不修改已有文本块，保证第一个块仍是可直接 <c>JSON.parse</c> 的正文；
    /// 只追加一次（重复调用不会叠加），且追加块长度 ≤ <see cref="MaxLength"/>。
    /// </summary>
    /// <param name="result">工具结果。</param>
    /// <param name="digestBody">摘要正文；空白时使用 <see cref="Fallback"/>。</param>
    /// <returns>原结果对象（原地修改，便于工具层直接 return）。</returns>
    public static ModelContextProtocol.Protocol.CallToolResult Append(
        ModelContextProtocol.Protocol.CallToolResult result,
        string? digestBody)
    {
        var body = string.IsNullOrWhiteSpace(digestBody) ? Fallback : digestBody.Trim();
        var line = Prefix + body;

        if (line.Length > MaxLength)
        {
            line = line[..(MaxLength - 1)] + "…";
        }

        foreach (var block in result.Content)
        {
            if (block is ModelContextProtocol.Protocol.TextContentBlock text &&
                text.Text.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return result;
            }
        }

        result.Content.Add(new ModelContextProtocol.Protocol.TextContentBlock { Text = line });
        return result;
    }
}
