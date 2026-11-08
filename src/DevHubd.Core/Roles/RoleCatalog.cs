using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevHubd.Core.Roles;

/// <summary>角色声明的加载结果：合法角色列表与逐条错误。错误非空即视为启动失败。</summary>
/// <param name="Roles">合法角色（按 id 排序）。</param>
/// <param name="Errors">可诊断错误，含文件与字段名。</param>
public sealed record RoleLoadResult(IReadOnlyList<RoleDefinition> Roles, IReadOnlyList<string> Errors);

/// <summary>
/// 角色声明加载器：读取 <c>plugin/roles/*.json</c>，按封闭字段与取值范围校验，
/// 并确认 <c>skills[]</c> 对应的 <c>plugin/skills/&lt;name&gt;/SKILL.md</c> 存在。
/// <para>
/// 校验口径与 <c>scripts/validate-plugin.mjs</c> 保持一致；区别是门禁在提交前拦，
/// 本加载器在<b>启动时</b>拦——声明文件坏了必须启动失败，不能静默跳过某个角色。
/// </para>
/// </summary>
public static class RoleCatalog
{
    private const string PluginDirectoryEnvironmentVariable = "DEVHUB_PLUGIN_DIR";

    private static readonly Regex _idPattern = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.Compiled);
    private static readonly Regex _toolNamePattern = new("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.Compiled);

    private static readonly string[] _allowedKeys =
    {
        "id", "name", "description", "skills", "tools", "delivery",
        "requiresApproval", "canRequestInput", "concurrency", "budget",
    };

    private static readonly string[] _allowedConcurrencyKeys = { "maxParallel", "maxAttempts" };
    private static readonly string[] _allowedBudgetKeys = { "maxRounds", "maxToolCalls", "maxDurationSeconds", "maxInputTokens" };
    private static readonly string[] _allowedDeliveries = { "auto", "dispatch_only" };

    /// <summary>
    /// 解析插件包目录。优先环境变量 <c>DEVHUB_PLUGIN_DIR</c>；否则从程序目录向上最多 8 层查找
    /// 含 <c>plugin/plugin.json</c> 的目录，覆盖开发/测试布局（bin 位于仓库内）。
    /// </summary>
    /// <param name="startDirectory">查找起点，默认 <see cref="AppContext.BaseDirectory"/>。</param>
    /// <returns>插件包绝对路径；未找到返回 null。</returns>
    public static string? ResolvePluginDirectory(string? startDirectory = null)
    {
        var configured = Environment.GetEnvironmentVariable(PluginDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured.Trim());
            return File.Exists(Path.Combine(full, "plugin.json")) ? full : null;
        }

        DirectoryInfo? current = new(startDirectory ?? AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "plugin");
            if (File.Exists(Path.Combine(candidate, "plugin.json")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 加载并校验全部角色声明。只要有一条错误，调用方必须拒绝启动。
    /// </summary>
    /// <param name="pluginDirectory">插件包绝对路径。</param>
    /// <returns>合法角色与错误列表。</returns>
    public static RoleLoadResult Load(string pluginDirectory)
    {
        var roles = new List<RoleDefinition>();
        var errors = new List<string>();

        var rolesDirectory = Path.Combine(pluginDirectory, "roles");
        if (!Directory.Exists(rolesDirectory))
        {
            errors.Add($"缺少 roles 目录：{rolesDirectory}");
            return new RoleLoadResult(roles, errors);
        }

        var skillNames = ReadSkillNames(pluginDirectory);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(rolesDirectory).OrderBy(item => item, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"roles/ 下只允许 .json 角色文件，发现：{fileName}");
                continue;
            }

            RoleDefinition? role;
            if (TryReadRole(path, fileName, skillNames, errors, out role))
            {
                if (!ids.Add(role!.Id))
                {
                    errors.Add($"角色 id 重复：{role.Id}");
                    continue;
                }

                roles.Add(role);
            }
        }

        if (roles.Count == 0 && errors.Count == 0)
        {
            errors.Add($"roles 目录为空：{rolesDirectory}");
        }

        roles.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return new RoleLoadResult(roles, errors);
    }

    /// <summary>列出 <c>plugin/skills/</c> 下真实存在的技能目录名。</summary>
    /// <param name="pluginDirectory">插件包绝对路径。</param>
    private static HashSet<string> ReadSkillNames(string pluginDirectory)
    {
        var skillsDirectory = Path.Combine(pluginDirectory, "skills");
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(skillsDirectory))
        {
            return names;
        }

        foreach (var directory in Directory.EnumerateDirectories(skillsDirectory))
        {
            var name = Path.GetFileName(directory);
            if (File.Exists(Path.Combine(directory, "SKILL.md")))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>读取并校验单个角色文件；失败时把可诊断原因写入 <paramref name="errors"/>。</summary>
    /// <param name="path">角色文件绝对路径。</param>
    /// <param name="fileName">角色文件名（含 .json）。</param>
    /// <param name="skillNames">已存在的技能目录名。</param>
    /// <param name="errors">错误收集器。</param>
    /// <param name="role">成功时的角色定义。</param>
    /// <returns>是否通过校验。</returns>
    private static bool TryReadRole(
        string path,
        string fileName,
        HashSet<string> skillNames,
        List<string> errors,
        out RoleDefinition? role)
    {
        role = null;
        var label = $"roles/{fileName}";
        var errorsBefore = errors.Count;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{label} 读取失败：{exception.Message}");
            return false;
        }

        var raw = System.Text.Encoding.UTF8.GetString(bytes);
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(raw);
        }
        catch (JsonException exception)
        {
            errors.Add($"{label} JSON 解析失败：{exception.Message}");
            return false;
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{label} 必须是 JSON 对象");
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!_allowedKeys.Contains(property.Name, StringComparer.Ordinal))
                {
                    errors.Add($"{label} 含封闭字段外的键：{property.Name}");
                }
            }

            foreach (var key in _allowedKeys)
            {
                if (!root.TryGetProperty(key, out _))
                {
                    errors.Add($"{label} 缺少必填字段：{key}");
                }
            }

            if (errors.Count != errorsBefore)
            {
                // 字段集合不完整时继续解析只会产生衍生错误，直接返回首个可诊断原因。
                return false;
            }

            var expectedId = Path.GetFileNameWithoutExtension(fileName);
            var id = ReadString(root, "id", label, errors);
            if (id is not null && (!_idPattern.IsMatch(id) || !string.Equals(id, expectedId, StringComparison.Ordinal)))
            {
                errors.Add($"{label} id 不合规或与文件名不一致：{id}（期望 {expectedId}）");
            }

            ReadRequiredText(root, "name", label, errors);
            ReadRequiredText(root, "description", label, errors);
            var skills = ReadStringArray(root, "skills", label, errors, null);
            if (skills is not null)
            {
                foreach (var skill in skills)
                {
                    if (!skillNames.Contains(skill))
                    {
                        errors.Add($"{label}.skills 引用不存在的技能：{skill}");
                    }
                }
            }

            var tools = ReadStringArray(root, "tools", label, errors, _toolNamePattern);
            var delivery = ReadString(root, "delivery", label, errors);
            if (delivery is not null && !_allowedDeliveries.Contains(delivery, StringComparer.Ordinal))
            {
                errors.Add($"{label}.delivery 必须是 auto 或 dispatch_only，实际：{delivery}");
            }

            var requiresApproval = ReadBoolean(root, "requiresApproval", label, errors);
            var canRequestInput = ReadBoolean(root, "canRequestInput", label, errors);

            var maxParallel = 0;
            var maxAttempts = 0;
            ReadConcurrency(root, label, errors, ref maxParallel, ref maxAttempts);

            var maxRounds = 0;
            var maxToolCalls = 0;
            var maxDurationSeconds = 0;
            int? maxInputTokens = null;
            ReadBudget(root, label, errors, ref maxRounds, ref maxToolCalls, ref maxDurationSeconds, ref maxInputTokens);

            if (errors.Count != errorsBefore)
            {
                return false;
            }

            role = new RoleDefinition(
                id!,
                root.GetProperty("name").GetString()!,
                root.GetProperty("description").GetString()!,
                skills!,
                tools!,
                delivery!,
                requiresApproval!.Value,
                canRequestInput!.Value,
                maxParallel,
                maxAttempts,
                maxRounds,
                maxToolCalls,
                maxDurationSeconds,
                maxInputTokens,
                path,
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                raw);
            return true;
        }
    }

    /// <summary>读取必填字符串；缺失或类型不符时记录错误。</summary>
    /// <param name="root">JSON 根对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="label">错误前缀。</param>
    /// <param name="errors">错误收集器。</param>
    /// <returns>字段值；不合法返回 null。</returns>
    private static string? ReadString(JsonElement root, string name, string label, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{label}.{name} 必须是字符串");
            return null;
        }

        return element.GetString();
    }

    /// <summary>读取必填非空文本字段。</summary>
    /// <param name="root">JSON 根对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="label">错误前缀。</param>
    /// <param name="errors">错误收集器。</param>
    private static void ReadRequiredText(JsonElement root, string name, string label, List<string> errors)
    {
        var value = ReadString(root, name, label, errors);
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{label}.{name} 必须是非空字符串");
        }
    }

    /// <summary>读取必填布尔字段。</summary>
    /// <param name="root">JSON 根对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="label">错误前缀。</param>
    /// <param name="errors">错误收集器。</param>
    /// <returns>字段值；不合法返回 null。</returns>
    private static bool? ReadBoolean(JsonElement root, string name, string label, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var element) ||
            (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False))
        {
            errors.Add($"{label}.{name} 必须是布尔");
            return null;
        }

        return element.GetBoolean();
    }

    /// <summary>读取非空字符串数组，可选校验元素命名规则。</summary>
    /// <param name="root">JSON 根对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="label">错误前缀。</param>
    /// <param name="errors">错误收集器。</param>
    /// <param name="pattern">可选取值规则。</param>
    /// <returns>元素数组；结构不合法返回 null。</returns>
    private static IReadOnlyList<string>? ReadStringArray(
        JsonElement root,
        string name,
        string label,
        List<string> errors,
        Regex? pattern)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{label}.{name} 必须是非空数组");
            return null;
        }

        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
            {
                errors.Add($"{label}.{name} 含非字符串元素");
                continue;
            }

            var value = item.GetString()!;
            if (pattern is not null && !pattern.IsMatch(value))
            {
                errors.Add($"{label}.{name} 含非法取值：{value}");
                continue;
            }

            items.Add(value);
        }

        if (items.Count == 0)
        {
            errors.Add($"{label}.{name} 必须是非空数组");
            return null;
        }

        return items;
    }

    /// <summary>校验 <c>concurrency</c> 对象：封闭键与整数范围。</summary>
    /// <param name="root">JSON 根对象。</param>
    /// <param name="label">错误前缀。</param>
    /// <param name="errors">错误收集器。</param>
    /// <param name="maxParallel">并行上限输出。</param>
    /// <param name="maxAttempts">重试上限输出。</param>
    private static void ReadConcurrency(
        JsonElement root,
        string label,
        List<string> errors,
        ref int maxParallel,
        ref int maxAttempts)
    {
        if (!root.TryGetProperty("concurrency", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{label}.concurrency 必须是对象");
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!_allowedConcurrencyKeys.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add($"{label}.concurrency 含非法键：{property.Name}");
            }
        }

        maxParallel = ReadInt(element, "maxParallel", $"{label}.concurrency", 1, 16, errors);
        maxAttempts = ReadInt(element, "maxAttempts", $"{label}.concurrency", 1, 10, errors);
    }

    /// <summary>校验 <c>budget</c> 对象：封闭键与整数范围。</summary>
    /// <param name="root">JSON 根对象。</param>
    /// <param name="label">错误前缀。</param>
    /// <param name="errors">错误收集器。</param>
    /// <param name="maxRounds">轮次上限输出。</param>
    /// <param name="maxToolCalls">工具调用上限输出。</param>
    /// <param name="maxDurationSeconds">墙钟上限输出。</param>
    /// <param name="maxInputTokens">输入 token 上限输出。</param>
    private static void ReadBudget(
        JsonElement root,
        string label,
        List<string> errors,
        ref int maxRounds,
        ref int maxToolCalls,
        ref int maxDurationSeconds,
        ref int? maxInputTokens)
    {
        if (!root.TryGetProperty("budget", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{label}.budget 必须是对象");
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!_allowedBudgetKeys.Contains(property.Name, StringComparer.Ordinal))
            {
                errors.Add($"{label}.budget 含非法键：{property.Name}");
            }
        }

        maxRounds = ReadInt(element, "maxRounds", $"{label}.budget", 1, 50, errors);
        maxToolCalls = ReadInt(element, "maxToolCalls", $"{label}.budget", 1, 200, errors);
        maxDurationSeconds = ReadInt(element, "maxDurationSeconds", $"{label}.budget", 30, 3600, errors);

        if (element.TryGetProperty("maxInputTokens", out var tokens))
        {
            if (tokens.ValueKind != JsonValueKind.Number || !tokens.TryGetInt32(out var value) || value < 1)
            {
                errors.Add($"{label}.budget.maxInputTokens 必须是正整数");
            }
            else
            {
                maxInputTokens = value;
            }
        }
    }

    /// <summary>读取范围内的整数字段。</summary>
    /// <param name="parent">父对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="label">错误前缀。</param>
    /// <param name="minimum">最小值（含）。</param>
    /// <param name="maximum">最大值（含）。</param>
    /// <param name="errors">错误收集器。</param>
    /// <returns>合法整数；否则返回 0 并记录错误。</returns>
    private static int ReadInt(JsonElement parent, string name, string label, int minimum, int maximum, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var value) ||
            value < minimum ||
            value > maximum)
        {
            errors.Add($"{label}.{name} 必须是 {minimum}–{maximum} 的整数");
            return 0;
        }

        return value;
    }
}
