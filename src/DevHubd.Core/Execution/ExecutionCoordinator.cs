using System.Collections.Concurrent;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using DevHubd.Core.Queue;
using DevHubd.Core.Tasks;
using DevHubd.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Execution;

/// <summary>
/// 执行协调器：认领 → 置 running → 心跳 → 跑回合 → 落 attempt/队列/结果/通知/熔断。
/// 并发由 <see cref="SemaphoreSlim"/> 控制（默认 1），因此两个任务严格串行；
/// 停机时取消在跑回合并按 <c>user_cancelled</c> 收尾，不留悬挂租约。
/// </summary>
public sealed class ExecutionCoordinator
{
    private static readonly int[] _transientBackoffSeconds = { 15, 30, 60 };

    private readonly QueueService _queue;
    private readonly ExecutionStore _store;
    private readonly ProviderStateStore _providerStates;
    private readonly WorkspaceRegistry _workspaces;
    private readonly TurnRunner _runner;
    private readonly ExecutionOptions _options;
    private readonly ILogger<ExecutionCoordinator> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, Task> _active = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _ownerId;

    /// <summary>注入执行依赖。</summary>
    /// <param name="queue">队列服务。</param>
    /// <param name="store">执行结果存储。</param>
    /// <param name="providerStates">provider 状态存取。</param>
    /// <param name="workspaces">工作区清单。</param>
    /// <param name="runner">回合执行器。</param>
    /// <param name="options">执行配置。</param>
    /// <param name="logger">日志器。</param>
    public ExecutionCoordinator(
        QueueService queue,
        ExecutionStore store,
        ProviderStateStore providerStates,
        WorkspaceRegistry workspaces,
        TurnRunner runner,
        ExecutionOptions options,
        ILogger<ExecutionCoordinator> logger)
    {
        _queue = queue;
        _store = store;
        _providerStates = providerStates;
        _workspaces = workspaces;
        _runner = runner;
        _options = options.Normalize();
        _logger = logger;
        _slots = new SemaphoreSlim(_options.MaxParallel, _options.MaxParallel);
        _ownerId = $"{Environment.MachineName}:{Environment.ProcessId}";
    }

    /// <summary>当前在跑任务数（观测与测试用）。</summary>
    public int ActiveCount => _active.Count;

    /// <summary>
    /// 认领并启动尽可能多的任务，直到达到并发上限或无可用队列项。
    /// </summary>
    /// <param name="cancellationToken">停机令牌。</param>
    /// <returns>本次启动的任务数。</returns>
    public async Task<int> PumpAsync(CancellationToken cancellationToken)
    {
        var started = 0;

        while (!cancellationToken.IsCancellationRequested && _active.Count < _options.MaxParallel)
        {
            if (!await _slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            ClaimedQueueItem? claimed;
            try
            {
                claimed = await _queue
                    .ClaimDetailedAsync(_ownerId, TimeSpan.FromSeconds(_options.LeaseSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                _slots.Release();
                throw;
            }

            if (claimed is null)
            {
                _slots.Release();
                break;
            }

            if (!await _providerStates.IsDispatchAllowedAsync(claimed.Item.PreferredProvider, cancellationToken)
                    .ConfigureAwait(false))
            {
                // 熔断打开：不消耗重试预算，退回队列并停止本轮认领，避免空转。
                await ReleaseBlockedAsync(claimed).ConfigureAwait(false);
                _slots.Release();
                _logger.LogWarning(
                    "provider 熔断打开，暂停派发：queueItemId={QueueItemId} provider={Provider}",
                    claimed.Item.Id,
                    claimed.Item.PreferredProvider);
                break;
            }

            await _store.MarkRunningAsync(claimed.Item.Id, Now(), cancellationToken).ConfigureAwait(false);

            var task = RunTrackedAsync(claimed, cancellationToken);
            _active[claimed.Item.Id] = task;
            started++;
        }

        return started;
    }

    /// <summary>等待在跑任务结束（停机时调用），取消后按 <c>user_cancelled</c> 收尾。</summary>
    /// <param name="cancellationToken">等待上限令牌。</param>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        var tasks = _active.Values.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning(exception, "等待在跑任务结束时超时，剩余任务将由租约回收。");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "在跑任务收尾时发生异常。");
        }
    }

    /// <summary>包装任务执行：统一释放并发槽位并从活动表移除。</summary>
    /// <param name="claimed">已认领项。</param>
    /// <param name="externalCancellationToken">外部停机令牌。</param>
    private Task RunTrackedAsync(ClaimedQueueItem claimed, CancellationToken externalCancellationToken) =>
        Task.Run(
            async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    externalCancellationToken,
                    _shutdown.Token);
                try
                {
                    await ExecuteItemAsync(claimed, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    await SettleCancelledAsync(claimed).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // 不允许吞异常：记录上下文；租约到期后由 reclaim 阶段兜底重派。
                    _logger.LogError(
                        exception,
                        "执行队列项失败：queueItemId={QueueItemId} attempt={Attempt} provider={Provider}",
                        claimed.Item.Id,
                        claimed.Item.Attempt,
                        claimed.Item.PreferredProvider);
                }
                finally
                {
                    _active.TryRemove(claimed.Item.Id, out _);
                    _slots.Release();
                }
            },
            CancellationToken.None);

    /// <summary>执行单个队列项：加载任务 → 心跳 → 回合 → 落库。</summary>
    /// <param name="claimed">已认领项。</param>
    /// <param name="cancellationToken">执行令牌。</param>
    private async Task ExecuteItemAsync(ClaimedQueueItem claimed, CancellationToken cancellationToken)
    {
        var item = claimed.Item;
        var loaded = await _workspaces.LoadAsync(cancellationToken).ConfigureAwait(false);
        var workspace = loaded.Workspaces.FirstOrDefault(
            candidate => candidate.Enabled && string.Equals(candidate.Id, item.WorkspaceId, StringComparison.Ordinal));

        if (workspace is null || !Directory.Exists(workspace.Root))
        {
            await SettleTaskMissingAsync(claimed, "TASK_WORKSPACE_MISSING", "工作区不存在或已禁用。")
                .ConfigureAwait(false);
            return;
        }

        var task = await _queue.GetTaskDocumentAsync(item.WorkspaceId, item.TaskId, cancellationToken)
            .ConfigureAwait(false);
        if (task is null)
        {
            await SettleTaskMissingAsync(claimed, "TASK_PAYLOAD_INVALID", "task_view.payload 无法解析为合法任务。")
                .ConfigureAwait(false);
            return;
        }

        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatLoopAsync(item, heartbeatStop, cancellationToken);

        TurnRunOutcome outcome;
        try
        {
            outcome = await _runner.RunAsync(item, task, workspace, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
        }

        var leaseHeld = await heartbeat.ConfigureAwait(false);
        if (!leaseHeld)
        {
            _logger.LogWarning(
                "租约已丢失，放弃落库：queueItemId={QueueItemId} attempt={Attempt}",
                item.Id,
                item.Attempt);
            return;
        }

        await SettleAsync(claimed, task, outcome).ConfigureAwait(false);
    }

    /// <summary>心跳循环：续租失败即取消回合；返回 false 表示租约丢失。</summary>
    /// <param name="item">队列项。</param>
    /// <param name="stop">用于取消回合的令牌源。</param>
    /// <param name="cancellationToken">执行令牌。</param>
    private async Task<bool> HeartbeatLoopAsync(
        QueueItem item,
        CancellationTokenSource stop,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.HeartbeatSeconds), linked.Token)
                    .ConfigureAwait(false);

                var renewed = await _queue
                    .HeartbeatAsync(item.Id, _ownerId, TimeSpan.FromSeconds(_options.LeaseSeconds), linked.Token)
                    .ConfigureAwait(false);

                if (!renewed)
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                    return false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常结束：回合完成或停机。
        }

        return true;
    }

    /// <summary>按回合结果落库：attempt 终态、队列迁移、结果与通知、provider 熔断。</summary>
    /// <param name="claimed">已认领项。</param>
    /// <param name="task">任务文档。</param>
    /// <param name="outcome">回合产出。</param>
    private async Task SettleAsync(ClaimedQueueItem claimed, TaskDocument task, TurnRunOutcome outcome)
    {
        // 收尾写库不受停机令牌影响，否则取消后无法记录终态。
        var cancellationToken = CancellationToken.None;
        var item = claimed.Item;
        var now = Now();
        var result = outcome.Result;

        if (result.State == TurnState.Done)
        {
            await _store.CompleteAttemptAsync(
                claimed.AttemptId,
                new AttemptCompletion(
                    "succeeded",
                    outcome.Model,
                    null,
                    null,
                    result.ToolCalls,
                    result.Usage?.InputTokens,
                    result.Usage?.OutputTokens,
                    result.FilesChanged.Count,
                    Truncate(result.Text)),
                now,
                cancellationToken).ConfigureAwait(false);

            await _store.SetTerminalAsync(item.Id, "succeeded", null, null, now, cancellationToken)
                .ConfigureAwait(false);
            await _providerStates.RecordSuccessAsync(outcome.Provider, cancellationToken).ConfigureAwait(false);

            var summary = string.IsNullOrWhiteSpace(result.Text) ? "回合完成，模型未返回文本。" : Truncate(result.Text);
            var resultId = await _store.InsertResultAsync(
                new ResultDraft(
                    item.TaskId,
                    item.Id,
                    outcome.Provider,
                    outcome.DeliveryOutcome,
                    summary,
                    outcome.Artifacts),
                now,
                cancellationToken).ConfigureAwait(false);

            await _store.InsertNoticeAsync(
                $"result:{item.Id}:{outcome.DeliveryOutcome}:{item.Attempt}",
                "result",
                item.TaskId,
                $"任务 {item.TaskId} 已产出结果（{outcome.DeliveryOutcome}）",
                summary,
                now,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "队列项执行成功：queueItemId={QueueItemId} attempt={Attempt} turnId={TurnId} provider={Provider} resultId={ResultId} outcome={Outcome}",
                item.Id,
                item.Attempt,
                outcome.TurnId,
                outcome.Provider,
                resultId,
                outcome.DeliveryOutcome);
            return;
        }

        var error = result.Error ?? new ModelError(
            ModelErrorKinds.ModelError,
            "UNKNOWN_ERROR",
            "回合失败但未提供错误信息。");

        await HandleFailureAsync(claimed, task, outcome, error, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>失败分流：按错误分类决定重试、熔断、转人工或终止。</summary>
    /// <param name="claimed">已认领项。</param>
    /// <param name="task">任务文档。</param>
    /// <param name="outcome">回合产出。</param>
    /// <param name="error">错误。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌（收尾固定为 None）。</param>
    private async Task HandleFailureAsync(
        ClaimedQueueItem claimed,
        TaskDocument task,
        TurnRunOutcome outcome,
        ModelError error,
        string now,
        CancellationToken cancellationToken)
    {
        var item = claimed.Item;
        var result = outcome.Result;

        await _store.CompleteAttemptAsync(
            claimed.AttemptId,
            new AttemptCompletion(
                result.State == TurnState.Cancelled ? "abandoned" : "failed",
                outcome.Model,
                error.Kind,
                error.Code,
                result.ToolCalls,
                result.Usage?.InputTokens,
                result.Usage?.OutputTokens,
                result.FilesChanged.Count,
                Truncate(error.Message)),
            now,
            cancellationToken).ConfigureAwait(false);

        switch (error.Kind)
        {
            case ModelErrorKinds.Quota:
            case ModelErrorKinds.Auth:
            case ModelErrorKinds.NoModel:
            case ModelErrorKinds.BridgeDown:
                await _providerStates
                    .RecordFailureAsync(outcome.Provider, error.Kind, error.Message, cancellationToken)
                    .ConfigureAwait(false);

                // 资源类失败不消耗重试预算；配额冷却 15 分钟，其余 60 秒后由探测/下一次认领恢复。
                var delay = error.Kind == ModelErrorKinds.Quota ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(60);
                await _store.RescheduleAsync(
                    item.Id,
                    DateTimeOffset.UtcNow.Add(delay).ToString("O"),
                    error.Code,
                    Truncate(error.Message),
                    decrementAttempt: true,
                    now,
                    cancellationToken).ConfigureAwait(false);

                await _store.InsertNoticeAsync(
                    $"breaker:{outcome.Provider}:{error.Kind}",
                    error.Kind switch
                    {
                        ModelErrorKinds.Quota => "breaker",
                        ModelErrorKinds.Auth => "auth",
                        _ => "provider",
                    },
                    item.TaskId,
                    $"provider {outcome.Provider} 不可用（{error.Kind}）",
                    error.Message,
                    now,
                    cancellationToken).ConfigureAwait(false);
                break;

            case ModelErrorKinds.BudgetExceeded:
            case ModelErrorKinds.UserCancelled:
                await _store.SetTerminalAsync(
                    item.Id,
                    error.Kind == ModelErrorKinds.UserCancelled ? "cancelled" : "dead",
                    error.Code,
                    Truncate(error.Message),
                    now,
                    cancellationToken).ConfigureAwait(false);

                await _store.InsertResultAsync(
                    new ResultDraft(
                        item.TaskId,
                        item.Id,
                        outcome.Provider,
                        "awaiting_review",
                        Truncate(error.Message),
                        outcome.Artifacts),
                    now,
                    cancellationToken).ConfigureAwait(false);

                await _store.InsertNoticeAsync(
                    $"failure:{item.Id}:{error.Code}:{item.Attempt}",
                    "failure",
                    item.TaskId,
                    $"任务 {item.TaskId} 需要人工处理（{error.Code}）",
                    error.Message,
                    now,
                    cancellationToken).ConfigureAwait(false);
                break;

            case ModelErrorKinds.LeaseLost:
                await _store.RescheduleAsync(
                    item.Id,
                    now,
                    error.Code,
                    Truncate(error.Message),
                    decrementAttempt: true,
                    now,
                    cancellationToken).ConfigureAwait(false);
                break;

            case ModelErrorKinds.ModelError:
                await RetryOrDieAsync(claimed, outcome, error, maxAttempts: 2, backoff: TimeSpan.FromSeconds(30), now, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                await RetryOrDieAsync(claimed, outcome, error, maxAttempts: item.MaxAttempts, backoff: null, now, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }

        _logger.LogWarning(
            "队列项执行失败：queueItemId={QueueItemId} attempt={Attempt} turnId={TurnId} provider={Provider} errorKind={ErrorKind} errorCode={ErrorCode}",
            item.Id,
            item.Attempt,
            outcome.TurnId,
            outcome.Provider,
            error.Kind,
            error.Code);
    }

    /// <summary>任务级失败：未达上限则退避重试，达上限则置 dead 并产出失败结果。</summary>
    /// <param name="claimed">已认领项。</param>
    /// <param name="outcome">回合产出。</param>
    /// <param name="error">错误。</param>
    /// <param name="maxAttempts">该错误分类允许的最大尝试次数。</param>
    /// <param name="backoff">固定退避；null 表示按尝试次数取 15/30/60 秒。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RetryOrDieAsync(
        ClaimedQueueItem claimed,
        TurnRunOutcome outcome,
        ModelError error,
        int maxAttempts,
        TimeSpan? backoff,
        string now,
        CancellationToken cancellationToken)
    {
        var item = claimed.Item;

        if (item.Attempt < maxAttempts)
        {
            var delay = backoff ?? TimeSpan.FromSeconds(
                _transientBackoffSeconds[Math.Min(item.Attempt - 1, _transientBackoffSeconds.Length - 1)]);

            await _store.RescheduleAsync(
                item.Id,
                DateTimeOffset.UtcNow.Add(delay).ToString("O"),
                error.Code,
                Truncate(error.Message),
                decrementAttempt: false,
                now,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await _store.SetTerminalAsync(
            item.Id,
            "dead",
            error.Code,
            Truncate(error.Message),
            now,
            cancellationToken).ConfigureAwait(false);

        await _store.InsertResultAsync(
            new ResultDraft(
                item.TaskId,
                item.Id,
                outcome.Provider,
                "failed",
                Truncate(error.Message),
                outcome.Artifacts),
            now,
            cancellationToken).ConfigureAwait(false);

        await _store.InsertNoticeAsync(
            $"failure:{item.Id}:{error.Code}:{item.Attempt}",
            "failure",
            item.TaskId,
            $"任务 {item.TaskId} 执行失败（{error.Code}）",
            error.Message,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>任务或工作区已不存在：直接终止，不消耗更多重试。</summary>
    /// <param name="claimed">已认领项。</param>
    /// <param name="code">错误码。</param>
    /// <param name="message">说明。</param>
    private async Task SettleTaskMissingAsync(ClaimedQueueItem claimed, string code, string message)
    {
        var cancellationToken = CancellationToken.None;
        var now = Now();

        await _store.CompleteAttemptAsync(
            claimed.AttemptId,
            new AttemptCompletion("failed", null, ModelErrorKinds.ModelError, code, 0, null, null, 0, message),
            now,
            cancellationToken).ConfigureAwait(false);

        await _store.SetTerminalAsync(claimed.Item.Id, "dead", code, message, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>停机时把在跑项收尾为 cancelled。</summary>
    /// <param name="claimed">已认领项。</param>
    private async Task SettleCancelledAsync(ClaimedQueueItem claimed)
    {
        var cancellationToken = CancellationToken.None;
        var now = Now();
        const string message = "进程停机，执行已取消。";

        await _store.CompleteAttemptAsync(
            claimed.AttemptId,
            new AttemptCompletion(
                "abandoned",
                null,
                ModelErrorKinds.UserCancelled,
                "SHUTDOWN",
                0,
                null,
                null,
                0,
                message),
            now,
            cancellationToken).ConfigureAwait(false);

        await _store.SetTerminalAsync(claimed.Item.Id, "cancelled", "SHUTDOWN", message, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>provider 熔断时退回队列：不消耗 attempt。</summary>
    /// <param name="claimed">已认领项。</param>
    private async Task ReleaseBlockedAsync(ClaimedQueueItem claimed)
    {
        var cancellationToken = CancellationToken.None;
        var now = Now();
        const string message = "provider 熔断打开，暂缓派发。";

        await _store.CompleteAttemptAsync(
            claimed.AttemptId,
            new AttemptCompletion(
                "abandoned",
                null,
                ModelErrorKinds.Quota,
                "PROVIDER_BLOCKED",
                0,
                null,
                null,
                0,
                message),
            now,
            cancellationToken).ConfigureAwait(false);

        await _store.RescheduleAsync(
            claimed.Item.Id,
            DateTimeOffset.UtcNow.AddSeconds(60).ToString("O"),
            "PROVIDER_BLOCKED",
            message,
            decrementAttempt: true,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>裁剪文本，避免 result.summary 无界增长。</summary>
    /// <param name="text">原文。</param>
    private static string Truncate(string text) =>
        text.Length <= 2000 ? text : text[..2000] + "…";

    /// <summary>当前 UTC 时间（ISO-8601）。</summary>
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
