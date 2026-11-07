using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Devhubd.Mcp.Tools;

/// <summary>
/// 工具返回体的 JSON 序列化约定：camelCase、忽略 null、中文不转义（便于 Chat 与日志直接阅读）。
/// 工具与测试共用同一套配置，避免字段名漂移。
/// </summary>
public static class ToolJson
{
    /// <summary>工具输出统一使用的序列化选项。</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}
