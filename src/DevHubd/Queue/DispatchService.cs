using DevHubd.Core.Providers;
using DevHubd.Core.Queue;
using DevHubd.Core.Scheduling;
using DevHubd.Core.Workspaces;
using DevHubd.Mcp.Abstractions;

namespace DevHubd.Queue;

/// <summary>
/// 手动派发实现：解析工作区与 provider、校验任务状态与调度开关，最后只做"入队"。
/// 显式指定 provider 时会校验其可用性（未配置 / 熔断 / 扩展桥离线一律拒绝）；
/// <c>auto</c> 优先选可用 provider，全不可用时回退到默认 provider 以便 dispatch_only 先入队。
/// </summary>
public sealed class DispatchService : IDispatchService
{
    private static readonly string[] _validProviders = { "auto", "openai", "vscode" };
    private static readonly string[] _providerPreference = { "openai", "vscode" };
    private const string DefaultProvider = "openai";

    private readonly WorkspaceRegistry _registry;
    private readonly QueueService _queue;
    private readonly SchedulerStateStore _stateStore;
    private readonly ProviderStateStore _providerStates;

    /// <summary>注入工作区清单、队列服务、调度状态存取与 provider 状态。</summary>
    /// <param name="registry">工作区清单。</param>
    /// <param name="queue">队列服务。</param>
    /// <param name="stateStore">调度状态存取。</param>
    /// <param name="providerStates">provider 状态存取。</param>
    public DispatchService(
        WorkspaceRegistry registry,
        QueueService queue,
        SchedulerStateStore stateStore,
        ProviderStateStore providerStates)
    {
        _registry = registry;
        _queue = queue;
        _stateStore = stateStore;
        _providerStates = providerStates;
    }

    /// <summary>把单个任务翻译成队列项。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="workspaceId">可选工作区 id。</param>
    /// <param name="provider">auto / openai / vscode。</param>
    /// <param name="dryRun">只判定不写入。</param>
    /// <param name="force">忽略 manual 暂停，并显式覆盖"已有活跃队列项或待审结果"的入队闸门。</param>
    /// <param name="requestId">幂等键；同键重复派发返回首次的队列项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<DispatchOutcome> DispatchNowAsync(
        string taskId,
        string? workspaceId,
        string provider,
        bool dryRun,
        bool force,
        string? requestId,
        CancellationToken cancellationToken = default)
    {
        var normalizedProvider = (provider ?? "auto").Trim().ToLowerInvariant();
        if (!_validProviders.Contains(normalizedProvider, StringComparer.Ordinal))
        {
            return new DispatchOutcome(DispatchStatus.InvalidProvider, workspaceId ?? string.Empty, null, null, null);
        }

        var state = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!state.Enabled)
        {
            // 只有 manual 暂停允许被 force 覆盖；quota/auth 等硬暂停必须显式恢复。
            var overridable = force && string.Equals(state.PauseReason, "manual", StringComparison.Ordinal);
            if (!overridable)
            {
                return new DispatchOutcome(DispatchStatus.SchedulerPaused, workspaceId ?? string.Empty, null, null, null);
            }
        }

        var resolved = normalizedProvider == "auto"
            ? await PickReadyProviderAsync(cancellationToken).ConfigureAwait(false) ?? DefaultProvider
            : normalizedProvider;

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return new DispatchOutcome(DispatchStatus.UnknownTask, workspaceId ?? string.Empty, null, null, null);
        }

        var loaded = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        var matches = await FindTasksAsync(loaded.Workspaces, taskId, workspaceId, cancellationToken).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return new DispatchOutcome(DispatchStatus.UnknownTask, workspaceId ?? string.Empty, null, null, null);
        }

        if (matches.Count > 1)
        {
            var candidates = matches.Select(match => match.Workspace.Id).ToArray();
            return new DispatchOutcome(DispatchStatus.AmbiguousTask, workspaceId ?? string.Empty, null, null, candidates);
        }

        var (workspace, task) = matches[0];
        if (!string.Equals(task.Status, "ready", StringComparison.Ordinal))
        {
            return new DispatchOutcome(DispatchStatus.TaskNotReady, workspace.Id, null, null, null);
        }

        // 任务本身合法后再校验 provider：未知 / 未就绪的任务优先报告自身问题，便于诊断。
        if (normalizedProvider != "auto" && !await IsProviderReadyAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            return new DispatchOutcome(DispatchStatus.ProviderUnavailable, workspace.Id, null, resolved, null);
        }

        var result = await _queue
            .EnqueueTaskAsync(workspace.Id, task, resolved, dryRun, requestId, force, cancellationToken)
            .ConfigureAwait(false);

        var status = result.Status switch
        {
            "deduped" => DispatchStatus.Deduped,
            "active_exists" => DispatchStatus.ActiveExists,
            "pending_review" => DispatchStatus.PendingReview,
            "delivered_exists" => DispatchStatus.DeliveredExists,
            "would_enqueue" => DispatchStatus.WouldEnqueue,
            _ => DispatchStatus.Enqueued,
        };

        // deduped / active_exists 时返回已存在队列项的真实 provider，避免把复用的队列项报成本次解析的 provider。
        return new DispatchOutcome(status, workspace.Id, result.QueueItemId, result.Provider ?? resolved, null);
    }

    /// <summary>
    /// 判断 provider 是否可用：状态必须为 ready 且熔断允许派发。
    /// 未落库（从未探测）视为不可用，避免把任务派给一个从未连通的 provider。
    /// </summary>
    /// <param name="provider">provider 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<bool> IsProviderReadyAsync(string provider, CancellationToken cancellationToken)
    {
        var record = await _providerStates.GetAsync(provider, cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.State, ProviderStates.Ready, StringComparison.Ordinal))
        {
            return false;
        }

        return await _providerStates.IsDispatchAllowedAsync(provider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按固定优先级挑一个可用 provider；全部不可用时返回 null（由调用方回退默认值）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<string?> PickReadyProviderAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providerPreference)
        {
            if (await IsProviderReadyAsync(provider, cancellationToken).ConfigureAwait(false))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// 在已启用工作区中定位任务。指定 <paramref name="workspaceId"/> 时只查该工作区，
    /// 否则跨工作区收集同名任务，用于判定歧义。
    /// </summary>
    /// <param name="workspaces">清单中的工作区。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="workspaceId">可选工作区 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<List<(WorkspaceDefinition Workspace, TaskRow Task)>> FindTasksAsync(
        IReadOnlyList<WorkspaceDefinition> workspaces,
        string taskId,
        string? workspaceId,
        CancellationToken cancellationToken)
    {
        var matches = new List<(WorkspaceDefinition Workspace, TaskRow Task)>();

        foreach (var workspace in workspaces)
        {
            if (!workspace.Enabled)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(workspaceId) &&
                !string.Equals(workspace.Id, workspaceId, StringComparison.Ordinal))
            {
                continue;
            }

            var task = await _queue.GetTaskAsync(workspace.Id, taskId, cancellationToken).ConfigureAwait(false);
            if (task is not null)
            {
                matches.Add((workspace, task));
            }
        }

        return matches;
    }
}
