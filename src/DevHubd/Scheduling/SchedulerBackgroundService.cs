using DevHubd.Core.Execution;
using DevHubd.Core.Scheduling;
using DevHubd.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevHubd.Scheduling;

/// <summary>
/// 调度后台循环：启动即扫描一次，之后按 <c>scheduler_state.interval_seconds</c> 周期执行，
/// 并接受 <see cref="SchedulerWakeSignal"/> 的即时唤醒（<c>scan_now</c>）。
/// 单次 tick 失败只记录并短暂退避，不终止宿主；取消时才退出循环。
/// </summary>
public sealed class SchedulerBackgroundService : BackgroundService
{
    private const int MinIntervalSeconds = 5;
    private const int MaxIntervalSeconds = 3600;

    private readonly SchedulerEngine _engine;
    private readonly SchedulerStateStore _stateStore;
    private readonly SchedulerWakeSignal _wake;
    private readonly ExecutionCoordinator _coordinator;
    private readonly DevHubdOptions _options;
    private readonly ILogger<SchedulerBackgroundService> _logger;

    /// <summary>注入调度引擎、状态存取、唤醒信号、执行协调器与启动选项。</summary>
    /// <param name="engine">调度引擎。</param>
    /// <param name="stateStore">调度状态存取。</param>
    /// <param name="wake">唤醒信号。</param>
    /// <param name="coordinator">执行协调器（tick 之后泵任务）。</param>
    /// <param name="options">启动选项（提供默认间隔）。</param>
    /// <param name="logger">日志器。</param>
    public SchedulerBackgroundService(
        SchedulerEngine engine,
        SchedulerStateStore stateStore,
        SchedulerWakeSignal wake,
        ExecutionCoordinator coordinator,
        DevHubdOptions options,
        ILogger<SchedulerBackgroundService> logger)
    {
        _engine = engine;
        _stateStore = stateStore;
        _wake = wake;
        _coordinator = coordinator;
        _options = options;
        _logger = logger;
    }

    /// <summary>后台循环主体。</summary>
    /// <param name="stoppingToken">宿主停止令牌。</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 让出线程，避免在宿主启动阶段同步阻塞。
        await Task.Yield();

        if (!_options.SchedulerEnabled)
        {
            _logger.LogWarning("调度被环境变量禁用（DEVHUB_SCHEDULER=off），后台循环不扫描、不派发。");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 预期路径：宿主停止。
            }

            await _coordinator.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("调度后台服务已启动。");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var state = await _stateStore.ReadAsync(stoppingToken).ConfigureAwait(false);
                var interval = Clamp(state.IntervalSeconds > 0 ? state.IntervalSeconds : _options.IntervalSeconds);

                var result = await _engine.TickAsync(stoppingToken).ConfigureAwait(false);

                // 单项失败已在协调器内按项隔离；这里兜住泵级异常，
                // 保证一次 tick 的扫描/统计不被中断，也不触发整轮 5 秒退避。
                var started = 0;
                try
                {
                    started = await _coordinator.PumpAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "执行泵异常，本轮 tick 已结束，下次 tick 继续。");
                }

                _logger.LogDebug(
                    "调度 tick 完成：skipped={Skipped} scanned={Scanned} enqueued={Enqueued} reclaimed={Reclaimed} started={Started}",
                    result.Skipped,
                    result.Scanned,
                    result.Enqueued,
                    result.Reclaimed,
                    started);

                await WaitForNextAsync(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 不允许吞异常：记录上下文后短暂退避重试，避免坏状态机把宿主打死。
                _logger.LogError(exception, "调度 tick 失败，5 秒后重试。");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // 停机：取消在跑回合并按 user_cancelled 收尾，避免留下悬挂租约。
        await _coordinator.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("调度后台服务已停止。");
    }

    /// <summary>等待固定间隔，或被 <see cref="SchedulerWakeSignal"/> 提前唤醒。</summary>
    /// <param name="intervalSeconds">间隔秒数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task WaitForNextAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(TimeSpan.FromSeconds(intervalSeconds), linked.Token);
        var wake = _wake.WaitAsync(linked.Token);

        await Task.WhenAny(delay, wake).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);

        await IgnoreCancellationAsync(delay).ConfigureAwait(false);
        await IgnoreCancellationAsync(wake).ConfigureAwait(false);
    }

    /// <summary>观察被取消的分支，避免未观察异常。</summary>
    /// <param name="task">待观察的任务。</param>
    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期路径：竞速中被取消的分支。
        }
    }

    /// <summary>把库里的间隔限制在合理区间，避免 0 或过大值造成空转。</summary>
    /// <param name="seconds">库中读取的间隔。</param>
    private static int Clamp(int seconds) => Math.Clamp(seconds, MinIntervalSeconds, MaxIntervalSeconds);
}
