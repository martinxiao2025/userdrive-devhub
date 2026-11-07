using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevHubd.Core.Workspaces;

/// <summary>工作区定义：`workspaces.json` 中的一条记录。</summary>
/// <param name="Id">工作区稳定标识，参与 dedupe_key 与 task_view 主键。</param>
/// <param name="Root">工作区根目录绝对路径。</param>
/// <param name="Enabled">是否参与扫描与派发。</param>
public sealed record WorkspaceDefinition(string Id, string Root, bool Enabled)
{
    /// <summary>任务文件目录：<c>&lt;root&gt;/.devhub/tasks</c>。</summary>
    public string TaskDirectory => Path.Combine(Root, ".devhub", "tasks");
}

/// <summary>一次加载的结果：可用工作区 + 被跳过的条目说明（供宿主记录 Warning）。</summary>
/// <param name="Workspaces">通过校验且启用的工作区。</param>
/// <param name="Problems">被跳过的条目与原因。</param>
public sealed record WorkspaceLoadResult(IReadOnlyList<WorkspaceDefinition> Workspaces, IReadOnlyList<string> Problems);

/// <summary>
/// 工作区清单读取器。清单位于 <c>%LOCALAPPDATA%\userdrive-devhub\workspaces.json</c>，
/// 不使用仓库内配置：同一台机器上的多个 VS Code 窗口共用一份清单。
/// 文件缺失视为"没有工作区"（不报错）；条目非法只跳过该条，不拖垮整份清单。
/// </summary>
public sealed class WorkspaceRegistry
{
    private static readonly Regex _idPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private readonly string _filePath;

    /// <summary>指定清单文件路径。</summary>
    /// <param name="filePath">清单绝对路径。</param>
    public WorkspaceRegistry(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>清单文件路径。</summary>
    public string FilePath => _filePath;

    /// <summary>读取并校验清单。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<WorkspaceLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return new WorkspaceLoadResult(Array.Empty<WorkspaceDefinition>(), Array.Empty<string>());
        }

        WorkspaceFile? file;
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            file = await JsonSerializer.DeserializeAsync<WorkspaceFile>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return new WorkspaceLoadResult(
                Array.Empty<WorkspaceDefinition>(),
                new[] { $"workspaces.json 无法解析：{exception.Message}" });
        }

        if (file is null || file.Version != 1)
        {
            return new WorkspaceLoadResult(
                Array.Empty<WorkspaceDefinition>(),
                new[] { "workspaces.json 的 version 必须为 1。" });
        }

        var workspaces = new List<WorkspaceDefinition>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in file.Workspaces)
        {
            if (!_idPattern.IsMatch(entry.Id))
            {
                problems.Add($"工作区 id 非法（需匹配 ^[a-z0-9][a-z0-9._-]{{0,63}}$）：{entry.Id}");
                continue;
            }

            if (!seen.Add(entry.Id))
            {
                problems.Add($"工作区 id 重复，已跳过后者：{entry.Id}");
                continue;
            }

            if (!Path.IsPathRooted(entry.Root))
            {
                problems.Add($"工作区 {entry.Id} 的 root 必须是绝对路径：{entry.Root}");
                continue;
            }

            workspaces.Add(new WorkspaceDefinition(entry.Id, Path.GetFullPath(entry.Root), entry.Enabled));
        }

        return new WorkspaceLoadResult(workspaces, problems);
    }

    /// <summary>`workspaces.json` 的文件结构。</summary>
    private sealed record WorkspaceFile(int Version, List<WorkspaceEntry> Workspaces);

    /// <summary>`workspaces.json` 中单条工作区记录。</summary>
    private sealed record WorkspaceEntry(string Id, string Root, bool Enabled = true);
}
