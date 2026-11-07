using System.Text.Json;
using DevHubd.Mcp.Tools;
using ModelContextProtocol.Protocol;

namespace DevHubd.Mcp.Errors;

/// <summary>
/// MCP 错误信封（§11.3）：<c>code / message / details / retryable / next</c>。
/// 工具层所有失败必须走这里，保证 Chat 侧的错误结构一致。
/// </summary>
public static class McpErrors
{
    /// <summary>
    /// 构造一个 <c>isError=true</c> 的工具结果。
    /// </summary>
    /// <param name="code">大写下划线错误码。</param>
    /// <param name="message">可诊断的中文消息。</param>
    /// <param name="retryable">是否值得重试。</param>
    /// <param name="next">下一步动作提示。</param>
    /// <param name="details">附加信息（可为匿名对象）。</param>
    public static CallToolResult Envelope(
        string code,
        string message,
        bool retryable,
        string? next = null,
        object? details = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message,
            ["details"] = details,
            ["retryable"] = retryable,
            ["next"] = next,
        };

        var text = JsonSerializer.Serialize(payload, ToolJson.Options);

        return new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
        };
    }
}
