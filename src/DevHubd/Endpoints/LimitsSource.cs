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
    private readonly DevHubdOptions _options;

    /// <summary>注入执行参数与启动选项。</summary>
    /// <param name="execution">执行参数。</param>
    /// <param name="options">启动选项。</param>
    public LimitsSource(ExecutionOptions execution, DevHubdOptions options)
    {
        _execution = execution;
        _options = options;
    }

    /// <summary>读取当前生效的上限。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<LimitsResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var budget = _execution.ToBudget();
        var limits = new LimitsResult(
            _options.DeliveryMode,
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
