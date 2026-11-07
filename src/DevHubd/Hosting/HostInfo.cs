using DevHubd.Core;

namespace DevHubd.Hosting;

/// <summary>
/// 宿主进程级信息，供 MCP 状态工具与健康端点复用。
/// </summary>
/// <param name="StartedAt">进程启动时间（UTC）。</param>
/// <param name="Version">程序集版本。</param>
/// <param name="Options">启动选项。</param>
/// <param name="Paths">运行时路径。</param>
public sealed record HostInfo(DateTimeOffset StartedAt, string Version, DevHubdOptions Options, DevHubPaths Paths)
{
    /// <summary>进程已运行秒数。</summary>
    public long UptimeSeconds => (long)Math.Max(0, (DateTimeOffset.UtcNow - StartedAt).TotalSeconds);
}
