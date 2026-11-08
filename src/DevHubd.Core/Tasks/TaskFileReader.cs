using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevHubd.Core.Tasks;

/// <summary>
/// 任务文件（<c>.devhub/tasks/*.json</c>）的解析结果。字段与 <c>schemas/task.schema.json</c> 对应。
/// </summary>
/// <param name="Id">任务 id，工作区内唯一。</param>
/// <param name="Title">标题。</param>
/// <param name="Status">ready / blocked / done / cancelled。</param>
/// <param name="Priority">0–1000，越大越先派发。</param>
/// <param name="Delivery">review / auto（全局 dispatch_only 时 auto 不生效）。</param>
/// <param name="WriteScope">相对工作区根的写白名单；空表示只读。</param>
/// <param name="Prompt">给模型的指令。</param>
/// <param name="Role">执行角色 id，缺省 <c>implementer</c>。</param>
/// <param name="DependsOn">前置任务 id 列表；任一未 <c>done</c> 时任务视图置为 <c>blocked</c>。</param>
/// <param name="RequiresApproval">产出是否必须人工审批后才算交付。</param>
/// <param name="Source">来源渠道（可选，仅用于追溯，不参与调度）。</param>
/// <param name="ContentHash">文件字节的 SHA-256（小写十六进制），用于 dedupe_key。</param>
/// <param name="RawJson">原始 JSON 文本，落 task_view.payload。</param>
public sealed record TaskDocument(
    string Id,
    string Title,
    string Status,
    int Priority,
    string Delivery,
    IReadOnlyList<string> WriteScope,
    string Prompt,
    string Role,
    IReadOnlyList<string> DependsOn,
    bool RequiresApproval,
    string? Source,
    string ContentHash,
    string RawJson);

/// <summary>
/// 任务文件读取器：严格按 schema 校验，任何字段不合法都返回 false 与可诊断原因，
/// 由扫描器跳过该文件并记日志，避免一个坏文件阻塞整个工作区。
/// </summary>
public static class TaskFileReader
{
    private static readonly Regex _idPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex _rolePattern = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.Compiled);
    private static readonly string[] _allowedStatuses = { "ready", "blocked", "done", "cancelled" };
    private static readonly string[] _allowedDeliveries = { "review", "auto" };
    private static readonly string[] _allowedProperties =
    {
        "id", "title", "status", "priority", "delivery", "writeScope", "prompt",
        "role", "dependsOn", "requiresApproval", "source",
    };

    /// <summary>读取并校验一个任务文件。</summary>
    /// <param name="path">任务文件绝对路径。</param>
    /// <param name="document">成功时的解析结果。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryRead(string path, out TaskDocument? document, out string? error)
    {
        document = null;
        error = null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"读取失败：{exception.Message}";
            return false;
        }

        var raw = System.Text.Encoding.UTF8.GetString(bytes);
        return TryParse(raw, out document, out error);
    }

    /// <summary>
    /// 解析任务 JSON 文本。执行器从 <c>task_view.payload</c> 还原任务时复用同一套校验，
    /// 保证"入队时合法的任务在执行时也按同一口径解析"。
    /// </summary>
    /// <param name="raw">原始 JSON 文本。</param>
    /// <param name="document">成功时的解析结果。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryParse(string raw, out TaskDocument? document, out string? error)
    {
        document = null;
        error = null;

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(raw);
        }
        catch (JsonException exception)
        {
            error = $"JSON 解析失败：{exception.Message}";
            return false;
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "任务文件必须是 JSON 对象。";
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!_allowedProperties.Contains(property.Name, StringComparer.Ordinal))
                {
                    error = $"不支持的字段：{property.Name}";
                    return false;
                }
            }

            if (!TryGetString(root, "id", out var id) || !_idPattern.IsMatch(id))
            {
                error = "id 缺失或不匹配 ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$";
                return false;
            }

            if (!TryGetString(root, "title", out var title) || title.Length is < 1 or > 200)
            {
                error = "title 缺失或长度不在 1–200";
                return false;
            }

            if (!TryGetString(root, "status", out var status) || !_allowedStatuses.Contains(status, StringComparer.Ordinal))
            {
                error = $"status 必须是 {string.Join('/', _allowedStatuses)}";
                return false;
            }

            var priority = 0;
            if (root.TryGetProperty("priority", out var priorityElement))
            {
                if (priorityElement.ValueKind != JsonValueKind.Number || !priorityElement.TryGetInt32(out priority) ||
                    priority is < 0 or > 1000)
                {
                    error = "priority 必须是 0–1000 的整数";
                    return false;
                }
            }

            var delivery = "review";
            if (root.TryGetProperty("delivery", out var deliveryElement))
            {
                delivery = deliveryElement.GetString() ?? string.Empty;
                if (!_allowedDeliveries.Contains(delivery, StringComparer.Ordinal))
                {
                    error = $"delivery 必须是 {string.Join('/', _allowedDeliveries)}";
                    return false;
                }
            }

            var writeScope = new List<string>();
            if (root.TryGetProperty("writeScope", out var scopeElement))
            {
                if (scopeElement.ValueKind != JsonValueKind.Array)
                {
                    error = "writeScope 必须是字符串数组";
                    return false;
                }

                foreach (var item in scopeElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        error = "writeScope 必须是字符串数组";
                        return false;
                    }

                    writeScope.Add(item.GetString()!);
                }
            }

            if (!TryGetString(root, "prompt", out var prompt) || prompt.Length < 1)
            {
                error = "prompt 缺失或为空";
                return false;
            }

            var role = "implementer";
            if (root.TryGetProperty("role", out var roleElement))
            {
                role = roleElement.GetString() ?? string.Empty;
                if (!_rolePattern.IsMatch(role))
                {
                    error = "role 不匹配 ^[a-z][a-z0-9-]{0,31}$";
                    return false;
                }
            }

            var dependsOn = new List<string>();
            if (root.TryGetProperty("dependsOn", out var dependsElement))
            {
                if (dependsElement.ValueKind != JsonValueKind.Array)
                {
                    error = "dependsOn 必须是任务 id 数组";
                    return false;
                }

                foreach (var item in dependsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        error = "dependsOn 必须是任务 id 数组";
                        return false;
                    }

                    var dependency = item.GetString()!;
                    if (!_idPattern.IsMatch(dependency))
                    {
                        error = $"dependsOn 含非法任务 id：{dependency}";
                        return false;
                    }

                    if (string.Equals(dependency, id, StringComparison.Ordinal))
                    {
                        error = "dependsOn 不能依赖自身";
                        return false;
                    }

                    if (dependsOn.Contains(dependency, StringComparer.Ordinal))
                    {
                        error = $"dependsOn 含重复任务 id：{dependency}";
                        return false;
                    }

                    dependsOn.Add(dependency);
                }
            }

            var requiresApproval = false;
            if (root.TryGetProperty("requiresApproval", out var approvalElement))
            {
                if (approvalElement.ValueKind != JsonValueKind.True && approvalElement.ValueKind != JsonValueKind.False)
                {
                    error = "requiresApproval 必须是布尔";
                    return false;
                }

                requiresApproval = approvalElement.GetBoolean();
            }

            string? source = null;
            if (root.TryGetProperty("source", out var sourceElement))
            {
                if (sourceElement.ValueKind != JsonValueKind.String)
                {
                    error = "source 必须是字符串";
                    return false;
                }

                source = sourceElement.GetString();
                if (source is { Length: > 200 })
                {
                    error = "source 长度不能超过 200";
                    return false;
                }
            }

            document = new TaskDocument(
                id,
                title,
                status,
                priority,
                delivery,
                writeScope,
                prompt,
                role,
                dependsOn,
                requiresApproval,
                source,
                Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))),
                raw);
            return true;
        }
    }

    /// <summary>读取必填字符串字段。</summary>
    /// <param name="root">JSON 根对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="value">字段值。</param>
    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }
}
