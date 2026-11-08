using DevHubd.Core.Models;

namespace DevHubd.Core.Execution;

/// <summary>
/// 执行器配置。默认值即设计文档 §10 的保守口径：单并发、10 分钟、只派发不交付。
/// 由用户级配置 <c>%LOCALAPPDATA%\userdrive-devhub\config.json</c> 的 <c>Execution</c> 节覆盖。
/// </summary>
public sealed record ExecutionOptions
{
    /// <summary>默认单并发。</summary>
    public const int DefaultMaxParallel = 1;

    /// <summary>并发上限（设计文档 §10.3：最大 4）。</summary>
    public const int MaxParallelLimit = 4;

    /// <summary>最大并发执行数。</summary>
    public int MaxParallel { get; init; } = DefaultMaxParallel;

    /// <summary>单回合最长时长（秒）。</summary>
    public int MaxDurationSeconds { get; init; } = 600;

    /// <summary>工具轮数上限。</summary>
    public int MaxToolRounds { get; init; } = 24;

    /// <summary>最多改动文件数。</summary>
    public int MaxFilesChanged { get; init; } = 20;

    /// <summary>最多写入次数。</summary>
    public int MaxWrites { get; init; } = 40;

    /// <summary>单次写入最大字节数（200 KB）。</summary>
    public long MaxPatchBytes { get; init; } = 200 * 1024;

    /// <summary>单次读取最大字节数（256 KB）。</summary>
    public int MaxReadBytes { get; init; } = 256 * 1024;

    /// <summary>交付档位：auto / dispatch_only。</summary>
    public string DeliveryMode { get; init; } = "dispatch_only";

    /// <summary>定时派发的默认 provider。</summary>
    public const string DefaultScheduledProvider = "openai";

    /// <summary>定时派发使用的 provider。</summary>
    public string ScheduledProvider { get; init; } = DefaultScheduledProvider;

    /// <summary>
    /// 归一化定时派发 provider：大小写不敏感，仅接受 <c>openai</c> / <c>vscode</c>。
    /// 非法值回退 <see cref="DefaultScheduledProvider"/>；调用方负责把原始值写成配置问题，
    /// 避免配置写错后静默改变派发通道。
    /// </summary>
    /// <param name="value">配置中的原始取值。</param>
    public static string NormalizeScheduledProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "vscode" => "vscode",
        _ => DefaultScheduledProvider,
    };

    /// <summary>租约 TTL（秒）。</summary>
    public int LeaseSeconds { get; init; } = 120;

    /// <summary>心跳间隔（秒）。</summary>
    public int HeartbeatSeconds { get; init; } = 30;

    /// <summary>问题单有效期（小时）：超时后置 <c>expired</c> 并让任务失败（<c>INPUT_TIMEOUT</c>）。</summary>
    public int InputTimeoutHours { get; init; } = 24;

    /// <summary>把执行配置收敛到合法区间，避免配置写错导致空转或越界。</summary>
    public ExecutionOptions Normalize() => this with
    {
        MaxParallel = Math.Clamp(MaxParallel, DefaultMaxParallel, MaxParallelLimit),
        MaxDurationSeconds = Math.Clamp(MaxDurationSeconds, 1, 3600),
        MaxToolRounds = Math.Clamp(MaxToolRounds, 1, 100),
        MaxFilesChanged = Math.Clamp(MaxFilesChanged, 1, 1000),
        MaxWrites = Math.Clamp(MaxWrites, 1, 1000),
        MaxPatchBytes = Math.Clamp(MaxPatchBytes, 1024, 8 * 1024 * 1024),
        MaxReadBytes = Math.Clamp(MaxReadBytes, 1024, 8 * 1024 * 1024),
        DeliveryMode = string.Equals(DeliveryMode, "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : "dispatch_only",
        ScheduledProvider = NormalizeScheduledProvider(ScheduledProvider),
        LeaseSeconds = Math.Clamp(LeaseSeconds, 30, 600),
        HeartbeatSeconds = Math.Clamp(HeartbeatSeconds, 5, 300),
        InputTimeoutHours = Math.Clamp(InputTimeoutHours, 1, 168),
    };

    /// <summary>转换为回合预算。</summary>
    public BudgetLimits ToBudget() => new(
        MaxToolRounds,
        TimeSpan.FromSeconds(MaxDurationSeconds),
        MaxFilesChanged,
        MaxWrites,
        MaxPatchBytes,
        MaxReadBytes);
}
