using DevHubd.Core.Execution;
using DevHubd.Core.Scheduling;
using DevHubd.Hosting;
using DevHubd.Mcp.Abstractions;

namespace DevHubd.Endpoints;

/// <summary>
/// 只读资源 <c>devhub://limits</c> 的数据源：把用户配置归一化后的执行上限与启动参数摊平。
/// 交付档位每次从 <c>scheduler_state.mode</c> 读取，避免上报与控制面内存值漂移。
/// </summary>
public sealed class LimitsSource : ILimitsSource
{
    private static readonly string[] _providers = { "openai", "vscode" };

    private readonly ExecutionOptions _execution;
    private readonly SchedulerStateStore _stateStore;
    private readonly DevHubdOptions _options;

    /// <summary>注入执行参数、调度状态存取与启动选项。</summary>
    /// <param name="execution">执行参数。</param>
    /// <param name="stateStore">调度状态存取（交付档位以库为准）。</param>
    /// <param name="options">启动选项。</param>
    public LimitsSource(ExecutionOptions execution, SchedulerStateStore stateStore, DevHubdOptions options)
    {
        _execution = execution;
        _stateStore = stateStore;
        _options = options;
    }

    /// <summary>读取当前生效的上限。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<LimitsResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var budget = _execution.ToBudget();
        var state = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new LimitsResult(
            // 上报实际生效的执行档位，而不是命令行解锁标记，避免观测值与行为不一致。
            state.Mode,
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
    }
}
