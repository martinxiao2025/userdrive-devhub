namespace DevHubd.Mcp.Abstractions;

/// <summary>运行期上限与白名单（只读资源 <c>devhub://limits</c> 使用）。</summary>
/// <param name="Mode">交付档位。</param>
/// <param name="SchedulerEnabled">调度是否启用。</param>
/// <param name="MaxParallel">最大并发执行数。</param>
/// <param name="MaxDurationSeconds">单回合预算秒数。</param>
/// <param name="MaxToolRounds">单回合工具轮数上限。</param>
/// <param name="MaxFilesChanged">单回合可写文件数上限。</param>
/// <param name="MaxWrites">单回合写操作上限。</param>
/// <param name="MaxPatchBytes">单次补丁字节上限。</param>
/// <param name="MaxReadBytes">单次读取字节上限。</param>
/// <param name="AllowedTools">工具白名单。</param>
/// <param name="Providers">provider 标识列表。</param>
public sealed record LimitsResult(
    string Mode,
    bool SchedulerEnabled,
    int MaxParallel,
    int MaxDurationSeconds,
    int MaxToolRounds,
    int MaxFilesChanged,
    int MaxWrites,
    long MaxPatchBytes,
    int MaxReadBytes,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> Providers);

/// <summary>运行期上限数据源。</summary>
public interface ILimitsSource
{
    /// <summary>读取当前生效的上限（来自用户配置 + 默认值归一化后的结果）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<LimitsResult> GetAsync(CancellationToken cancellationToken = default);
}
