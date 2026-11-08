using DevHubd.Core.Execution;
using DevHubd.Core.Input;
using DevHubd.Core.Queue;
using DevHubd.Core.Tasks;
using DevHubd.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Scheduling;

/// <summary>一次扫描 tick 的观测结果。</summary>
/// <param name="Skipped">是否因调度暂停而跳过扫描。</param>
/// <param name="Workspaces">参与扫描的工作区数。</param>
/// <param name="Scanned">读取的任务文件数。</param>
/// <param name="Invalid">非法任务文件数。</param>
/// <param name="Enqueued">新增队列项数。</param>
/// <param name="Deduped">按 dedupe_key 命中去重数。</param>
/// <param name="SkippedActive">因已有活动队列项而跳过数。</param>
/// <param name="Reclaimed">回收的过期租约数。</param>
public sealed record SchedulerTickResult(
    bool Skipped,
    int Workspaces,
    int Scanned,
    int Invalid,
    int Enqueued,
    int Deduped,
    int SkippedActive,
    int Reclaimed);

/// <summary>
/// 调度引擎：一次 tick = 回收过期租约 → 读取工作区清单 → 扫描任务 → 按 dedupe_key 幂等入队 → 落状态与计数。
/// 只做"把意图翻译成队列项"，不执行任务、不调用模型（执行在 P2）。
/// </summary>
public sealed class SchedulerEngine
{
    private readonly WorkspaceRegistry _registry;
    private readonly TaskScanner _scanner;
    private readonly QueueService _queue;
    private readonly SchedulerStateStore _stateStore;
    private readonly ExecutionOptions _executionOptions;
    private readonly InputRequestStore _inputRequests;
    private readonly ILogger<SchedulerEngine> _logger;

    /// <summary>注入依赖。</summary>
    /// <param name="registry">工作区清单。</param>
    /// <param name="scanner">任务扫描器。</param>
    /// <param name="queue">队列服务。</param>
    /// <param name="stateStore">调度状态存取。</param>
    /// <param name="executionOptions">执行配置（提供定时派发的 provider）。</param>
    /// <param name="inputRequests">问题单存取（任务变更时取消旧挂起项）。</param>
    /// <param name="logger">日志器。</param>
    public SchedulerEngine(
        WorkspaceRegistry registry,
        TaskScanner scanner,
        QueueService queue,
        SchedulerStateStore stateStore,
        ExecutionOptions executionOptions,
        InputRequestStore inputRequests,
        ILogger<SchedulerEngine> logger)
    {
        _registry = registry;
        _scanner = scanner;
        _queue = queue;
        _stateStore = stateStore;
        _executionOptions = executionOptions;
        _inputRequests = inputRequests;
        _logger = logger;
    }

    /// <summary>执行一次完整的扫描与入队。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerTickResult> TickAsync(CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        var reclaimed = await _queue.ReclaimExpiredAsync(cancellationToken).ConfigureAwait(false);

        if (reclaimed > 0)
        {
            _logger.LogWarning("回收过期租约 {Count} 条，队列项回到 pending。", reclaimed);
        }

        if (!state.Enabled)
        {
            var now = DateTimeOffset.UtcNow;
            await _stateStore.SaveScanAsync(
                now.ToString("O"),
                now.AddSeconds(state.IntervalSeconds).ToString("O"),
                new Dictionary<string, long> { ["reclaimed"] = reclaimed },
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("调度已暂停，跳过扫描。pauseReason={PauseReason}", state.PauseReason);
            return new SchedulerTickResult(true, 0, 0, 0, 0, 0, 0, reclaimed);
        }

        var loaded = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        var scheduledProvider = ExecutionOptions.NormalizeScheduledProvider(_executionOptions.ScheduledProvider);
        foreach (var problem in loaded.Problems)
        {
            _logger.LogWarning("工作区清单条目被跳过：{Problem}", problem);
        }

        var workspaces = 0;
        var scanned = 0;
        var invalid = 0;
        var enqueued = 0;
        var deduped = 0;
        var skippedActive = 0;

        foreach (var workspace in loaded.Workspaces)
        {
            if (!workspace.Enabled)
            {
                continue;
            }

            if (!Directory.Exists(workspace.Root))
            {
                _logger.LogWarning("工作区根目录不存在，已跳过：id={WorkspaceId} root={Root}", workspace.Id, workspace.Root);
                continue;
            }

            workspaces++;

            var scan = await _scanner.ScanAsync(workspace, cancellationToken).ConfigureAwait(false);
            scanned += scan.Scanned;
            invalid += scan.Invalid;

            foreach (var problem in scan.Problems)
            {
                _logger.LogWarning("任务文件被跳过：workspace={WorkspaceId} {Problem}", workspace.Id, problem);
            }

            // 任务内容变化后旧问题单不能再回答（旧答案会答到新任务上），
            // 必须在入队前取消，否则旧挂起项会一直占着"活动队列项"使新内容无法入队。
            var cancelled = await _inputRequests.CancelStaleAsync(workspace.Id, cancellationToken)
                .ConfigureAwait(false);
            if (cancelled > 0)
            {
                _logger.LogWarning(
                    "任务内容已变更，取消旧问题单 {Count} 条：workspace={WorkspaceId}",
                    cancelled,
                    workspace.Id);
            }

            // 超时问题单不能无限期挂着任务：置 expired 并把挂起队列项置 failed(INPUT_TIMEOUT)。
            var expired = await _inputRequests.ExpireOverdueAsync(workspace.Id, cancellationToken)
                .ConfigureAwait(false);
            if (expired > 0)
            {
                _logger.LogWarning(
                    "问题单等待回答超时 {Count} 条：workspace={WorkspaceId}",
                    expired,
                    workspace.Id);
            }

            var enqueue = await _queue
                .EnqueueReadyTasksAsync(workspace.Id, scheduledProvider, dryRun: false, cancellationToken)
                .ConfigureAwait(false);
            enqueued += enqueue.Enqueued;
            deduped += enqueue.Deduped;
            skippedActive += enqueue.SkippedActive;
        }

        var completedAt = DateTimeOffset.UtcNow;
        await _stateStore.SaveScanAsync(
            completedAt.ToString("O"),
            completedAt.AddSeconds(state.IntervalSeconds).ToString("O"),
            new Dictionary<string, long>
            {
                ["scanned"] = scanned,
                ["invalid"] = invalid,
                ["enqueued"] = enqueued,
                ["deduped"] = deduped,
                ["reclaimed"] = reclaimed,
            },
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "扫描完成：workspaces={Workspaces} scanned={Scanned} invalid={Invalid} enqueued={Enqueued} deduped={Deduped} skippedActive={SkippedActive} reclaimed={Reclaimed}",
            workspaces,
            scanned,
            invalid,
            enqueued,
            deduped,
            skippedActive,
            reclaimed);

        return new SchedulerTickResult(false, workspaces, scanned, invalid, enqueued, deduped, skippedActive, reclaimed);
    }
}
