using System.Text;
using System.Text.Json;
using DevHubd.Core;
using DevHubd.Logging;
using Microsoft.Extensions.Logging;

namespace DevHubd.Hosting;

/// <summary>
/// <c>runtime.json</c> 写入器：C# 侧写、TS 扩展侧读，用于发现 MCP 端口与（P3 起的）内部 API 令牌。
/// 采用"临时文件 + 原子替换"，避免扩展读到半截 JSON。
/// </summary>
public static class RuntimeFileWriter
{
    /// <summary>
    /// 原子写出发现文件。文件位于 %LOCALAPPDATA%\userdrive-devhub，目录 ACL 默认仅当前用户与 SYSTEM 可访问。
    /// 内部 API 与 MCP 共用回环端口，令牌每次启动重新生成。
    /// </summary>
    /// <param name="paths">运行时路径。</param>
    /// <param name="options">启动选项。</param>
    /// <param name="deliveryMode">实际生效的交付档位（来自用户配置，而不是命令行解锁标记）。</param>
    /// <param name="startedAt">进程启动时间。</param>
    /// <param name="apiToken">内部 API 令牌。</param>
    public static void Write(
        DevHubPaths paths,
        DevHubdOptions options,
        string deliveryMode,
        DateTimeOffset startedAt,
        string apiToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            mcpPort = options.McpPort,
            mcpUrl = $"http://{options.BindAddress}:{options.McpPort}/mcp",
            apiPort = options.McpPort,
            apiEnabled = true,
            token = apiToken,
            startedAt = startedAt.ToString("O"),
            version = typeof(RuntimeFileWriter).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            deliveryMode = deliveryMode,
            dataDirectory = paths.DataDirectory,
        }, new JsonSerializerOptions { WriteIndented = true });

        WriteAtomic(paths.RuntimeFilePath, payload);
    }

    /// <summary>进程退出时清理发现文件，避免扩展连接到已死进程。清理失败只记日志，不阻断退出。</summary>
    /// <param name="paths">运行时路径。</param>
    /// <param name="logger">进程级日志器。</param>
    public static void Delete(DevHubPaths paths, ILogger logger)
    {
        try
        {
            if (File.Exists(paths.RuntimeFilePath))
            {
                File.Delete(paths.RuntimeFilePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DevHubLog.RuntimeFileDeleteFailed(logger, paths.RuntimeFilePath, exception);
        }
    }

    /// <summary>写临时文件后整体替换目标文件。</summary>
    /// <param name="targetPath">目标文件绝对路径。</param>
    /// <param name="content">要写入的文本内容。</param>
    private static void WriteAtomic(string targetPath, string content)
    {
        var directory = Path.GetDirectoryName(targetPath)
                        ?? throw new InvalidOperationException($"无法解析目录：{targetPath}");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Environment.ProcessId}.tmp");
        File.WriteAllBytes(tempPath, Encoding.UTF8.GetBytes(content));
        File.Move(tempPath, targetPath, overwrite: true);
    }
}
