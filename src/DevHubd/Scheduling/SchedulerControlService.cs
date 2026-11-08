using DevHubd.Core.Execution;
using DevHubd.Core.Providers;
using DevHubd.Core.Scheduling;
using DevHubd.Hosting;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using Microsoft.Extensions.Logging;

namespace DevHubd.Scheduling;

/// <summary>
/// 调度控制面实现：启动 / 暂停 / 恢复 / 立即扫描 / 切换档位 / 重置熔断 / 探测 provider，
/// 每个动作都写 <c>audit</c>。暂停只影响扫描与入队，不影响已认领任务的执行。
/// </summary>
public sealed class SchedulerControlService : ISchedulerControl
{
    private const string Actor = "mcp";

    private static readonly string[] _validModes = { "auto", "dispatch_only" };

    private readonly SchedulerStateStore _stateStore;
    private readonly SchedulerWakeSignal _wake;
    private readonly ProviderStateStore _providerStates;
    private readonly ModelClientRegistry _clients;
    private readonly DevHubdOptions _options;
    private readonly EffectiveDeliveryMode _deliveryMode;
    private readonly ILogger<SchedulerControlService> _logger;

    /// <summary>注入状态存取、唤醒信号、provider 状态、客户端注册表、启动选项、运行时档位与日志器。</summary>
    /// <param name="stateStore">调度状态存取。</param>
    /// <param name="wake">唤醒信号。</param>
    /// <param name="providerStates">provider 状态存取。</param>
    /// <param name="clients">模型客户端注册表。</param>
    /// <param name="options">启动选项（决定档位锁与环境硬关）。</param>
    /// <param name="deliveryMode">运行时交付档位单例，写库成功后同步更新。</param>
    /// <param name="logger">日志器。</param>
    public SchedulerControlService(
        SchedulerStateStore stateStore,
        SchedulerWakeSignal wake,
        ProviderStateStore providerStates,
        ModelClientRegistry clients,
        DevHubdOptions options,
        EffectiveDeliveryMode deliveryMode,
        ILogger<SchedulerControlService> logger)
    {
        _stateStore = stateStore;
        _wake = wake;
        _providerStates = providerStates;
        _clients = clients;
        _options = options;
        _deliveryMode = deliveryMode;
        _logger = logger;
    }

    /// <summary>读取当前控制面状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerControlSnapshot> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(state, scanRequested: false, changed: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>启动调度；环境变量硬关时拒绝。</summary>
    /// <param name="reason">启动原因，写入审计。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerControlSnapshot> StartAsync(
        string? reason,
        CancellationToken cancellationToken = default)
    {
        EnsureSchedulerNotHardOff();
        var before = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        await _stateStore.SetEnabledAsync(true, null, cancellationToken).ConfigureAwait(false);
        await _stateStore
            .AuditAsync(Actor, "scheduler.start", "scheduler_state", $"reason={reason ?? "manual"}", cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("调度已启动：reason={Reason}", reason ?? "manual");
        _wake.Request();

        var after = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(after, scanRequested: false, changed: !before.Enabled, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>暂停调度（默认原因 manual），并写审计。</summary>
    /// <param name="reason">暂停原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerControlSnapshot> PauseAsync(
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var effective = string.IsNullOrWhiteSpace(reason) ? "manual" : reason;
        var before = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        await _stateStore.SetEnabledAsync(false, effective, cancellationToken).ConfigureAwait(false);
        await _stateStore.AuditAsync(Actor, "scheduler.pause", "scheduler_state", $"reason={effective}", cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("调度已暂停：reason={Reason}", effective);
        var after = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(after, scanRequested: false, changed: before.Enabled, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>恢复调度，并写审计。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerControlSnapshot> ResumeAsync(CancellationToken cancellationToken = default)
    {
        EnsureSchedulerNotHardOff();
        var before = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        await _stateStore.SetEnabledAsync(true, null, cancellationToken).ConfigureAwait(false);
        await _stateStore.AuditAsync(Actor, "scheduler.resume", "scheduler_state", null, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("调度已恢复。");
        _wake.Request();

        var after = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(after, scanRequested: false, changed: !before.Enabled, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>请求立即扫描一次：写入唤醒信号，由后台循环执行。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerControlSnapshot> RequestScanAsync(CancellationToken cancellationToken = default)
    {
        await _stateStore.AuditAsync(Actor, "scheduler.scan_now", "scheduler_state", null, cancellationToken)
            .ConfigureAwait(false);
        _wake.Request();

        _logger.LogInformation("已请求立即扫描。");
        var state = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(state, scanRequested: true, changed: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 切换交付档位。<c>auto</c> 需要启动时显式解锁（<c>--allow-auto-delivery</c>），
    /// 否则返回 <c>MODE_LOCKED</c>，避免 MCP 侧绕过启动参数提升权限。
    /// </summary>
    /// <param name="mode">目标档位。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerControlSnapshot> SetModeAsync(
        string mode,
        CancellationToken cancellationToken = default)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (!_validModes.Contains(normalized, StringComparer.Ordinal))
        {
            throw new SchedulerControlException(
                "INVALID_MODE",
                $"未知交付档位：{mode}",
                "使用 auto 或 dispatch_only。",
                retryable: true);
        }

        if (normalized == "auto" && !_options.AllowAutoDelivery)
        {
            throw new SchedulerControlException(
                "MODE_LOCKED",
                "本进程以 dispatch_only 启动，未解锁 auto 档位。",
                "用 --allow-auto-delivery 重启 devhubd 后再切换。");
        }

        var before = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        await _stateStore.SetModeAsync(normalized, cancellationToken).ConfigureAwait(false);

        // 写库成功后同步运行时单例：执行侧、健康检查与 limits 立即看到新档位，
        // 不再出现"库已切换、执行仍按配置跑"的空转。
        _deliveryMode.Set(normalized);
        await _stateStore.AuditAsync(Actor, "scheduler.set_mode", "scheduler_state", $"mode={normalized}", cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("交付档位已切换：from={From} to={To}", before.Mode, normalized);
        var after = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(
                after,
                scanRequested: false,
                changed: !string.Equals(before.Mode, normalized, StringComparison.Ordinal),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>人工重置 provider 熔断；熔断未打开时返回 <c>NOT_PAUSED</c>。</summary>
    /// <param name="provider">provider 标识。</param>
    /// <param name="reason">重置原因，写入审计。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerControlSnapshot> ResetBreakerAsync(
        string provider,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (_clients.Get(provider) is null)
        {
            throw new SchedulerControlException(
                "INVALID_PROVIDER",
                $"未知 provider：{provider}",
                "使用 openai 或 vscode。",
                retryable: true);
        }

        var reset = await _providerStates.ResetBreakerAsync(provider, cancellationToken).ConfigureAwait(false);
        if (!reset)
        {
            throw new SchedulerControlException(
                "NOT_PAUSED",
                $"provider {provider} 的熔断未打开，无需重置。",
                "用 scheduler_status 查看 providers[].breaker 后再决定。");
        }

        await _stateStore
            .AuditAsync(Actor, "provider.reset_breaker", provider, $"reason={reason ?? "manual"}", cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("provider 熔断已重置：provider={Provider} reason={Reason}", provider, reason ?? "manual");
        var state = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(state, scanRequested: false, changed: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>立即探测所有 provider 并落库；探测不打开熔断。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerControlSnapshot> ProbeModelsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _clients.Providers)
        {
            var client = _clients.Get(provider);
            if (client is null)
            {
                continue;
            }

            var health = await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
            var current = await _providerStates.GetAsync(provider, cancellationToken).ConfigureAwait(false);
            await _providerStates.SaveAsync(
                new ProviderStateRecord(
                    provider,
                    health.State,
                    health.Detail,
                    current?.Breaker ?? BreakerStates.Closed,
                    current?.BreakerReason,
                    current?.BreakerOpenedAt,
                    current?.ConsecutiveFailures ?? 0,
                    DateTimeOffset.UtcNow.ToString("O")),
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "provider 探测完成：provider={Provider} state={State} available={Available}",
                provider,
                health.State,
                health.Available);
        }

        await _stateStore.AuditAsync(Actor, "provider.probe_models", null, null, cancellationToken)
            .ConfigureAwait(false);

        var state = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(state, scanRequested: false, changed: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>环境变量硬关（<c>DEVHUB_SCHEDULER=off</c>）时拒绝启动类动作。</summary>
    private void EnsureSchedulerNotHardOff()
    {
        if (!_options.SchedulerEnabled)
        {
            throw new SchedulerControlException(
                "SCHEDULER_PAUSED_BY_ENV",
                "调度被环境变量禁用（DEVHUB_SCHEDULER=off）。",
                "移除该环境变量并重启 devhubd。");
        }
    }

    /// <summary>把调度状态映射为控制面快照，并附带 provider 状态。</summary>
    /// <param name="state">调度状态。</param>
    /// <param name="scanRequested">本次动作是否请求了立即扫描。</param>
    /// <param name="changed">本次动作是否改变了状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<SchedulerControlSnapshot> ToSnapshotAsync(
        SchedulerState state,
        bool scanRequested,
        bool changed,
        CancellationToken cancellationToken)
    {
        var providers = await _providerStates.ListAsync(cancellationToken).ConfigureAwait(false);
        return new SchedulerControlSnapshot
        {
            Enabled = state.Enabled,
            Mode = state.Mode,
            PauseReason = state.PauseReason,
            IntervalSeconds = state.IntervalSeconds,
            ScanRequested = scanRequested,
            Changed = changed,
            Providers = providers
                .Select(record => new ProviderStatus
                {
                    Provider = record.Provider,
                    State = record.State,
                    Breaker = record.Breaker,
                    Detail = record.Detail,
                    CheckedAt = record.CheckedAt,
                })
                .ToArray(),
        };
    }
}
