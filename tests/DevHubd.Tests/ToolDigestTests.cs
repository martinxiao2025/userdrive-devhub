using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Tools;
using Xunit;

namespace DevHubd.Tests;

/// <summary>工具尾部摘要（§11.4）的格式、幂等与截断契约。</summary>
public sealed class ToolDigestTests
{
    /// <summary>摘要作为独立文本块追加，正文块保持原样，且不会重复追加。</summary>
    [Fact]
    public void Append_AddsSeparateLineOnce()
    {
        var result = ToolResults.Text("{\"ok\":true}");

        ToolDigest.Append(result, "未读 1 · 队列 待派发 2");
        ToolDigest.Append(result, "未读 1 · 队列 待派发 2");

        Assert.Equal(2, result.Content.Count);

        var payload = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(result.Content[0]).Text;
        Assert.Equal("{\"ok\":true}", payload);

        var digest = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(result.Content[1]).Text;
        Assert.Equal($"{ToolDigest.Prefix}未读 1 · 队列 待派发 2", digest);
    }

    /// <summary>追加块（含前缀）不超过 240 字符，并保留省略号。</summary>
    [Fact]
    public void Append_TruncatesToLimit()
    {
        var result = ToolResults.Text("{}");
        ToolDigest.Append(result, new string('长', 500));

        var line = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(result.Content[^1]).Text;

        Assert.True(line.Length <= ToolDigest.MaxLength, $"摘要长度 {line.Length} 超过上限");
        Assert.EndsWith("…", line, StringComparison.Ordinal);
    }

    /// <summary>空摘要降级为固定文案，保证"每个工具都有摘要"。</summary>
    [Fact]
    public void Append_UsesFallbackWhenEmpty()
    {
        var result = ToolResults.Text("{}");
        ToolDigest.Append(result, "   ");

        var line = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(result.Content[^1]).Text;
        Assert.Equal(ToolDigest.Prefix + ToolDigest.Fallback, line);
    }
}
