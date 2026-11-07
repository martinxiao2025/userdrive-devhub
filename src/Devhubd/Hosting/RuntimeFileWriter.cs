using System.Text;
using System.Text.Json;
using Devhubd.Core;

namespace Devhubd.Hosting;

/// <summary>
/// <c>runtime.json</c> 写入器：C# 侧写、TS 扩展侧读，用于发现 MCP 端口与（P3 起的）内部 API 令牌。
/// 采用"临时文件 + 原子替换"，避免扩展读到半截 JSON。
/// </summary>
public static class RuntimeFileWriter
{
    /// <summary>
    /// 原子写出发现文件。文件位于 %LOCALAPPDATA%\userdrive-devhub，目录 ACL 默认仅当前用户与 SYSTEM 可访问。
    /// </summary>
    /// <param name="paths">运行时路径。</param>
    /// <param name="options">启动选项。</param>
    /// <param name="startedAt">进程启动时间。</param>
    public static void Write(DevhubPaths paths, DevhubdOptions options, DateTimeOffset startedAt)
    {
        var payload = JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            mcpPort = options.McpPort,
            mcpUrl = $"http://{options.BindAddress}:{options.McpPort}/mcp",
            apiPort = 0,
            apiEnabled = false,
            token = (string?)null,
            startedAt = startedAt.ToString("O"),
            version = typeof(RuntimeFileWriter).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            deliveryMode = options.DeliveryMode,
            dataDirectory = paths.DataDirectory,
        }, new JsonSerializerOptions { WriteIndented = true });

        WriteAtomic(paths.RuntimeFilePath, payload);
    }

    /// <summary>进程退出时清理发现文件，避免扩展连接到已死进程。</summary>
    /// <param name="paths">运行时路径。</param>
    public static void Delete(DevhubPaths paths)
    {
        try
        {
            if (File.Exists(paths.RuntimeFilePath))
            {
                File.Delete(paths.RuntimeFilePath);
            }
        }
        catch (IOException)
        {
            // 清理失败不应影响退出流程。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>写临时文件后整体替换目标文件。</summary>
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
