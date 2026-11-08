using DevHubd.Core.Execution;
using DevHubd.Hosting;
using DevHubd.Mcp.Abstractions;

namespace DevHubd.Endpoints;

/// <summary>
/// 只读资源 <c>devhub://limits</c> 的数据源：把用户配置归一化后的执行上限与启动参数摊平。
/// </summary>
public sealed class LimitsSource : ILimitsSource
{
    private static readonly string[] _providers = { "openai", "vscode" };

    private readonly ExecutionOptions _execution;
    private readonly EffectiveDeliveryMode _deliveryMode;
    private readonly DevHubdOptions _options;

    /// <summary>注入执行参数、运行时档位与启动选项。</summary>
    /// <param name="execution">执行参数。</param>
    /// <param name="deliveryMode">运行时交付档位单例。</param>
    /// <param name="options">启动选项。</param>
    public LimitsSource(ExecutionOptions execution, EffectiveDeliveryMode deliveryMode, DevHubdOptions options)
    {
        _execution = execution;
        _deliveryMode = deliveryMode;
        _options = options;
    }

    /// <summary>读取当前生效的上限。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<LimitsResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var budget = _execution.ToBudget();
        var limits = new LimitsResult(
            // 上报实际生效的执行档位，而不是命令行解锁标记，避免观测值与行为不一致。
            _deliveryMode.Value,
            _options.SchedulerEnabled,
            _execution.MaxParallel,
            (int)budget.MaxDuration.TotalSeconds,
            budget.MaxToolRounds,
            budget.MaxFilesChanged,
            budget.MaxWrites,
            budget.MaxPatchBytes,
            budget.MaxReadBytes,
            ToolDispatcher.AllowedToolNames,
            _providers);

        return Task.FromResult(limits);
    }
}
