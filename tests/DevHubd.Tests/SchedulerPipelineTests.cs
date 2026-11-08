using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Execution;
using DevHubd.Core.Input;
using DevHubd.Core.Providers;
using DevHubd.Core.Queue;
using DevHubd.Core.Results;
using DevHubd.Core.Scheduling;
using DevHubd.Core.Tasks;
using DevHubd.Core.Workspaces;
using DevHubd.Mcp.Abstractions;
using DevHubd.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreResultDecisionStatus = DevHubd.Core.Results.ResultDecisionStatus;

namespace DevHubd.Tests;

/// <summary>
/// P1 调度流水线验收：同一文件重复扫描只入队一次、内容变更生成新 dedupe_key、多工作区隔离，
/// 以及 <c>dispatch_now</c> 背后的判定语义。
/// </summary>
public sealed class SchedulerPipelineTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteConnectionFactory _factory;
    private readonly QueueService _queue;
    private readonly SchedulerStateStore _stateStore;
    private readonly SchedulerEngine _engine;
    private readonly WorkspaceRegistry _registry;

    /// <summary>准备临时数据目录、数据库、工作区清单与调度引擎。</summary>
    public SchedulerPipelineTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _registry = new WorkspaceRegistry(Path.Combine(_directory, "workspaces.json"));
        _queue = new QueueService(_factory);
        _stateStore = new SchedulerStateStore(_factory);
        _engine = new SchedulerEngine(
            _registry,
            new TaskScanner(_factory),
            _queue,
            _stateStore,
            new ExecutionOptions(),
            new InputRequestStore(_factory),
            NullLogger<SchedulerEngine>.Instance);
    }

    /// <summary>清理临时目录；SQLite 连接池可能短暂占用文件，失败不视为测试失败。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }
    }

    /// <summary>同一任务文件扫描 100 次，队列中始终只有 1 项。</summary>
    [Fact]
    public async Task TickAsync_RepeatedScans_KeepSingleQueueItemAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "第一次", "ready", 10);

        for (var index = 0; index < 100; index++)
        {
            await _engine.TickAsync(ct);
        }

        Assert.Equal(1, await CountQueueItemsAsync(ct));
        var state = await _stateStore.ReadAsync(ct);
        Assert.Equal(100, state.Counters["scanned"]);
        Assert.Equal(1, state.Counters["enqueued"]);
    }

    /// <summary>任务完成后内容变更：生成新的 dedupe_key 与新的队列项。</summary>
    [Fact]
    public async Task TickAsync_AfterContentChange_CreatesNewDedupeKeyAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "第一次", "ready", 10);

        await _engine.TickAsync(ct);
        var firstKeys = await ReadDedupeKeysAsync(ct);
        Assert.Single(firstKeys);

        // 模拟上一轮已完成，否则"同一任务已有活动项"会正确阻止重复入队。
        await ExecuteAsync("UPDATE queue_item SET status = 'succeeded';", ct);

        WriteTask(root, "T-001", "第二次（内容已改）", "ready", 10);
        await _engine.TickAsync(ct);

        var secondKeys = await ReadDedupeKeysAsync(ct);
        Assert.Equal(2, secondKeys.Count);
        Assert.Equal(2, secondKeys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// 已交付任务不允许被文件里的旧状态退回可执行：即使库里是 done/approved、文件仍是 ready，
    /// 扫描保持 done，入队也不会再派发。
    /// </summary>
    [Fact]
    public async Task TickAsync_DeliveredTaskWhoseFileRegressed_IsNotReEnqueuedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "已交付任务", "ready", 10);

        await _engine.TickAsync(ct);
        Assert.Equal(1, await CountQueueItemsAsync(ct));

        // 模拟旧版本遗留的半边态：审批已落库，任务文件未同步。
        await ExecuteAsync(
            """
            UPDATE queue_item SET status = 'succeeded';
            UPDATE task_view SET status = 'done', approval_state = 'approved';
            """,
            ct);

        await _engine.TickAsync(ct);

        Assert.Equal(1, await CountQueueItemsAsync(ct));
        Assert.Equal(
            new[] { "done|approved" },
            await ReadColumnAsync("SELECT status || '|' || approval_state FROM task_view WHERE id = 'T-001';", ct));
    }

    /// <summary>
    /// 待审任务跨三个 tick 只执行一次：入队闸门以结果状态（awaiting_review / delivered）判定，
    /// 即使队列项已终态、任务文件仍是 ready，也不得再派发同一份内容。
    /// </summary>
    [Fact]
    public async Task TickAsync_PendingReviewAcrossThreeTicks_EnqueuesOnceAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "待审任务", "ready", 10);

        await _engine.TickAsync(ct);
        Assert.Equal(1, await CountQueueItemsAsync(ct));

        // 模拟回合完成但结果待审：队列项终态、结果 awaiting_review、任务文件保持 ready。
        var queueItemId = (await ReadColumnAsync("SELECT id FROM queue_item;", ct)).Single();
        await InsertAwaitingResultAsync(queueItemId, "res-1", ct);
        await ExecuteAsync("UPDATE queue_item SET status = 'succeeded';", ct);

        await _engine.TickAsync(ct);
        await _engine.TickAsync(ct);
        await _engine.TickAsync(ct);

        Assert.Equal(1, await CountQueueItemsAsync(ct));
        Assert.Equal(1, await CountRowsAsync("SELECT COUNT(*) FROM result;", ct));
    }

    /// <summary>
    /// 重派去重不依赖任务文件字节：同一任务连续两轮"执行→驳回→重派"都必须真的入队，
    /// 即使驳回回写后的文件字节完全一致；去重键形如 <c>hash:r&lt;已驳回次数&gt;</c>。
    /// </summary>
    [Fact]
    public async Task TickAsync_AfterTwoRejections_RedispatchesWithUnchangedFileBytesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "连续驳回", "ready", 10);
        var file = Path.Combine(root, ".devhub", "tasks", "T-001.json");
        var decisions = new ResultDecisionStore(_factory);

        await _engine.TickAsync(ct);
        Assert.Equal(1, await CountQueueItemsAsync(ct));

        // 第一轮：回合完成并进入待审，随后驳回（走真实审批存储，写审计与通知）。
        var firstQueue = (await ReadColumnAsync("SELECT id FROM queue_item;", ct)).Single();
        await InsertAwaitingResultAsync(firstQueue, "res-1", ct);
        await ExecuteAsync($"UPDATE queue_item SET status = 'succeeded' WHERE id = '{firstQueue}';", ct);
        var firstDecision = await decisions.DecideAsync("res-1", approve: false, "第一轮驳回", null, ct);
        Assert.Equal(CoreResultDecisionStatus.Decided, firstDecision.Status);

        // 驳回回写后的文件即为规范字节形态，记录哈希用于第二轮对比。
        var canonicalHash = await ComputeHashAsync(file, ct);
        await _engine.TickAsync(ct);
        Assert.Equal(2, await CountQueueItemsAsync(ct));

        // 第二轮：文件字节不变，仍必须重派。
        var secondQueue = (await ReadColumnAsync(
            "SELECT id FROM queue_item WHERE status = 'pending';", ct)).Single();
        Assert.Equal(canonicalHash, await ComputeHashAsync(file, ct));
        await InsertAwaitingResultAsync(secondQueue, "res-2", ct);
        await ExecuteAsync($"UPDATE queue_item SET status = 'succeeded' WHERE id = '{secondQueue}';", ct);
        var secondDecision = await decisions.DecideAsync("res-2", approve: false, "第二轮驳回", null, ct);
        Assert.Equal(CoreResultDecisionStatus.Decided, secondDecision.Status);

        await _engine.TickAsync(ct);
        Assert.Equal(3, await CountQueueItemsAsync(ct));
        Assert.Equal(canonicalHash, await ComputeHashAsync(file, ct));

        var keys = await ReadDedupeKeysAsync(ct);
        Assert.Equal(3, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(keys, key => key.EndsWith(":r0", StringComparison.Ordinal));
        Assert.Contains(keys, key => key.EndsWith(":r1", StringComparison.Ordinal));
        Assert.Contains(keys, key => key.EndsWith(":r2", StringComparison.Ordinal));
    }

    /// <summary>
    /// 已审批任务的重做通道：任务文件仅改 <c>status</c> 不触发重派；
    /// 实质字段（prompt 等）变化才视为新需求，重置审批态并重新入队。
    /// </summary>
    [Fact]
    public async Task TickAsync_ApprovedTask_RedispatchesOnlyOnSubstantiveChangeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "已交付任务", "ready", 10, prompt: "原始需求");
        var file = Path.Combine(root, ".devhub", "tasks", "T-001.json");

        await _engine.TickAsync(ct);
        Assert.Equal(1, await CountQueueItemsAsync(ct));

        // 模拟审批已通过：库 done/approved，文件被回写成 done。
        await ExecuteAsync(
            """
            UPDATE queue_item SET status = 'succeeded';
            UPDATE task_view SET status = 'done', approval_state = 'approved';
            """,
            ct);
        Assert.True(await TaskFileStatusUpdater.TrySetStatusAsync(file, "done", ct));

        // 仅 status 改回 ready：不是新需求，不得重派。
        Assert.True(await TaskFileStatusUpdater.TrySetStatusAsync(file, "ready", ct));
        await _engine.TickAsync(ct);
        Assert.Equal(1, await CountQueueItemsAsync(ct));
        Assert.Equal(
            new[] { "done|approved" },
            await ReadColumnAsync("SELECT status || '|' || approval_state FROM task_view WHERE id = 'T-001';", ct));

        // 实质字段变化：视为新需求，审批态重置并重派。
        WriteTask(root, "T-001", "已交付任务", "ready", 10, prompt: "新的验收条件");
        await _engine.TickAsync(ct);
        Assert.Equal(2, await CountQueueItemsAsync(ct));
        Assert.Equal(
            new[] { "ready|none" },
            await ReadColumnAsync("SELECT status || '|' || approval_state FROM task_view WHERE id = 'T-001';", ct));
    }

    /// <summary>两个工作区存在同名任务：task_view 与队列项都按 workspace 隔离。</summary>
    [Fact]
    public async Task TickAsync_TwoWorkspaces_WithSameTaskId_StayIsolatedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var alpha = CreateWorkspace("alpha");
        var beta = CreateWorkspace("beta");
        WriteWorkspaces(("alpha", alpha), ("beta", beta));
        WriteTask(alpha, "T-001", "Alpha 的任务", "ready", 10);
        WriteTask(beta, "T-001", "Beta 的任务", "ready", 10);

        await _engine.TickAsync(ct);

        Assert.Equal(2, await CountQueueItemsAsync(ct));

        var workspaces = await ReadColumnAsync("SELECT workspace_id FROM queue_item ORDER BY workspace_id;", ct);
        Assert.Equal(new[] { "alpha", "beta" }, workspaces);

        var alphaTask = await _queue.GetTaskAsync("alpha", "T-001", ct);
        var betaTask = await _queue.GetTaskAsync("beta", "T-001", ct);
        Assert.Equal("Alpha 的任务", alphaTask!.Title);
        Assert.Equal("Beta 的任务", betaTask!.Title);
    }

    /// <summary>同名任务跨工作区时，未指定 workspaceId 的手动派发必须返回歧义与候选列表。</summary>
    [Fact]
    public async Task DispatchNow_AmbiguousTask_ReturnsCandidatesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var alpha = CreateWorkspace("alpha");
        var beta = CreateWorkspace("beta");
        WriteWorkspaces(("alpha", alpha), ("beta", beta));
        WriteTask(alpha, "T-001", "Alpha 的任务", "ready", 10);
        WriteTask(beta, "T-001", "Beta 的任务", "ready", 10);
        await _engine.TickAsync(ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", null, "auto", dryRun: false, force: false, requestId: null, ct);

        Assert.Equal(DispatchStatus.AmbiguousTask, outcome.Status);
        Assert.NotNull(outcome.Candidates);
        Assert.Equal(new[] { "alpha", "beta" }, outcome.Candidates!.OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>任务尚未入队时，指定 workspaceId 的手动派发会入队，provider=auto 解析为 openai。</summary>
    [Fact]
    public async Task DispatchNow_WithWorkspace_EnqueuesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "唯一任务", "ready", 10);

        // 只扫描不调度：task_view 有行、队列为空，用于验证手动派发的新增路径。
        await new TaskScanner(_factory).ScanAsync(new WorkspaceDefinition("alpha", root, true), ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "auto", dryRun: false, force: false, requestId: null, ct);

        Assert.Equal(DispatchStatus.Enqueued, outcome.Status);
        Assert.Equal("alpha", outcome.WorkspaceId);
        Assert.Equal("openai", outcome.Provider);
        Assert.False(string.IsNullOrEmpty(outcome.QueueItemId));
        Assert.Equal(1, await CountQueueItemsAsync(ct));
    }

    /// <summary>
    /// U12 验收②：同一 <c>requestId</c> 重复派发返回首次创建的队列项，不会重复插入。
    /// </summary>
    [Fact]
    public async Task DispatchNow_SameRequestId_ReturnsSameQueueItemAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "唯一任务", "ready", 10);
        await new TaskScanner(_factory).ScanAsync(new WorkspaceDefinition("alpha", root, true), ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));

        var first = await dispatcher.DispatchNowAsync(
            "T-001", "alpha", "auto", dryRun: false, force: false, requestId: "req-1", ct);
        var second = await dispatcher.DispatchNowAsync(
            "T-001", "alpha", "auto", dryRun: false, force: false, requestId: "req-1", ct);

        Assert.Equal(DispatchStatus.Enqueued, first.Status);
        Assert.Equal(DispatchStatus.Deduped, second.Status);
        Assert.Equal(first.QueueItemId, second.QueueItemId);
        Assert.Equal(1, await CountQueueItemsAsync(ct));
    }

    /// <summary>调度已把同一内容的队列项入队后，手动派发是幂等的：返回 Deduped 而不是重复插入。</summary>
    [Fact]
    public async Task DispatchNow_WhenAlreadyEnqueued_ReturnsDedupedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "唯一任务", "ready", 10);
        await _engine.TickAsync(ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "auto", dryRun: false, force: false, requestId: null, ct);

        Assert.Equal(DispatchStatus.Deduped, outcome.Status);
        Assert.Equal("openai", outcome.Provider);
        Assert.Equal(1, await CountQueueItemsAsync(ct));
    }

    /// <summary>
    /// 命中已存在的队列项时，返回该队列项的真实 provider，而不是本次请求解析出的 provider；
    /// 否则 Chat 侧会把"复用 vscode 队列项"误读成"新派给了 openai"。
    /// </summary>
    [Fact]
    public async Task DispatchNow_WhenAlreadyEnqueued_ReturnsExistingProviderAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "唯一任务", "ready", 10);
        await new TaskScanner(_factory).ScanAsync(new WorkspaceDefinition("alpha", root, true), ct);

        var task = await _queue.GetTaskAsync("alpha", "T-001", ct);
        var enqueued = await _queue.EnqueueTaskAsync("alpha", task!, "vscode", dryRun: false, requestId: null, ct);
        Assert.Equal("vscode", enqueued.Provider);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "auto", dryRun: false, force: false, requestId: null, ct);

        Assert.Equal(DispatchStatus.Deduped, outcome.Status);
        Assert.Equal("vscode", outcome.Provider);
        Assert.Equal(enqueued.QueueItemId, outcome.QueueItemId);
        Assert.Equal(1, await CountQueueItemsAsync(ct));
    }

    /// <summary>任务不是 ready 时拒绝派发。</summary>
    [Fact]
    public async Task DispatchNow_TaskNotReady_ReturnsTaskNotReadyAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "被阻塞的任务", "blocked", 10);
        await _engine.TickAsync(ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "openai", dryRun: true, force: false, requestId: null, ct);

        Assert.Equal(DispatchStatus.TaskNotReady, outcome.Status);
    }

    /// <summary>provider 非法时返回 InvalidProvider，不做任何写入。</summary>
    [Fact]
    public async Task DispatchNow_InvalidProvider_ReturnsInvalidProviderAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "唯一任务", "ready", 10);
        await _engine.TickAsync(ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "claude", dryRun: false, force: false, requestId: null, ct);

        Assert.Equal(DispatchStatus.InvalidProvider, outcome.Status);
    }

    /// <summary>调度暂停时手动派发返回 SchedulerPaused。</summary>
    [Fact]
    public async Task DispatchNow_WhenPaused_ReturnsSchedulerPausedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "唯一任务", "ready", 10);
        await _stateStore.SetEnabledAsync(false, "manual", ct);

        var dispatcher = new DispatchService(_registry, _queue, _stateStore, new ProviderStateStore(_factory));
        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "openai", dryRun: false, force: false, requestId: null, ct);

        Assert.Equal(DispatchStatus.SchedulerPaused, outcome.Status);
    }

    /// <summary>默认配置（未写 Execution:ScheduledProvider）下，定时入队写 openai，与改动前行为一致。</summary>
    [Fact]
    public async Task TickAsync_DefaultScheduledProvider_EnqueuesOpenAiAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "默认 provider", "ready", 10);

        await _engine.TickAsync(ct);

        Assert.Equal(
            new[] { "openai" },
            await ReadColumnAsync("SELECT preferred_provider FROM queue_item;", ct));
    }

    /// <summary>显式配置 <c>Execution:ScheduledProvider=vscode</c> 时，定时入队写 vscode。</summary>
    [Fact]
    public async Task TickAsync_ScheduledProviderVscode_EnqueuesVscodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "vscode provider", "ready", 10);

        await CreateEngine(new ExecutionOptions { ScheduledProvider = "vscode" }).TickAsync(ct);

        Assert.Equal(
            new[] { "vscode" },
            await ReadColumnAsync("SELECT preferred_provider FROM queue_item;", ct));
    }

    /// <summary>非法 provider 回退 openai（不阻断扫描，也不把非法值写进队列）。</summary>
    [Fact]
    public async Task TickAsync_InvalidScheduledProvider_FallsBackToOpenAiAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "非法 provider", "ready", 10);

        await CreateEngine(new ExecutionOptions { ScheduledProvider = "gemini" }).TickAsync(ct);

        Assert.Equal(
            new[] { "openai" },
            await ReadColumnAsync("SELECT preferred_provider FROM queue_item;", ct));
    }

    /// <summary>前置任务未 done 时，后置任务在 task_view 中为 blocked 且不入队；前置 done 后自动解除。</summary>
    [Fact]
    public async Task TickAsync_DependsOnUnfinished_BlocksUntilDependencyDoneAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "前置", "ready", 10);
        WriteTask(root, "T-002", "后置", "ready", 10, dependsOn: new[] { "T-001" });

        await _engine.TickAsync(ct);

        Assert.Equal("blocked", (await _queue.GetTaskAsync("alpha", "T-002", ct))!.Status);
        Assert.Equal(1, await CountQueueItemsAsync(ct));
        Assert.Equal(
            new[] { "T-001" },
            await ReadColumnAsync("SELECT task_id FROM queue_item;", ct));

        // 前置任务标记 done 后，下一轮扫描从文件状态重新推导：后置恢复 ready 并入队。
        WriteTask(root, "T-001", "前置", "done", 10);
        await _engine.TickAsync(ct);

        Assert.Equal("ready", (await _queue.GetTaskAsync("alpha", "T-002", ct))!.Status);
        Assert.Equal(2, await CountQueueItemsAsync(ct));
    }

    /// <summary>任务声明的角色写入 task_view，并在入队时复制到 queue_item.role_id。</summary>
    [Fact]
    public async Task TickAsync_TaskRole_IsCopiedToTaskViewAndQueueItemAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "评审任务", "ready", 10, role: "reviewer");

        await _engine.TickAsync(ct);

        Assert.Equal(
            new[] { "reviewer" },
            await ReadColumnAsync("SELECT role_id FROM task_view;", ct));
        Assert.Equal(
            new[] { "reviewer" },
            await ReadColumnAsync("SELECT role_id FROM queue_item;", ct));
    }

    /// <summary>角色未在 role_view 声明时任务被 blocked，绝不静默降级为 implementer。</summary>
    [Fact]
    public async Task TickAsync_UnknownRole_BlocksTaskAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        await ExecuteAsync(
            """
            INSERT INTO role_view
              (id, name, description, source_path, content_hash, skills, tools, delivery,
               requires_approval, can_request_input, max_parallel, max_attempts, max_rounds,
               max_tool_calls, max_duration_seconds, max_input_tokens, payload, loaded_at, updated_at)
            VALUES
              ('implementer', '实现', 'desc', 'p', 'h', '[]', '[]', 'dispatch_only',
               0, 0, 1, 3, 10, 20, 300, NULL, '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """,
            ct);
        WriteTask(root, "T-001", "未知角色", "ready", 10, role: "ghost");

        await _engine.TickAsync(ct);

        Assert.Equal("blocked", (await _queue.GetTaskAsync("alpha", "T-001", ct))!.Status);
        Assert.Equal(0, await CountQueueItemsAsync(ct));
    }

    /// <summary>用指定执行配置创建调度引擎（共享同一数据库与工作区清单）。</summary>
    /// <param name="options">执行配置。</param>
    private SchedulerEngine CreateEngine(ExecutionOptions options) => new(
        _registry,
        new TaskScanner(_factory),
        _queue,
        _stateStore,
        options,
        new InputRequestStore(_factory),
        NullLogger<SchedulerEngine>.Instance);

    /// <summary>创建工作区根目录（含 .devhub/tasks）。</summary>
    /// <param name="id">工作区 id。</param>
    private string CreateWorkspace(string id)
    {
        var root = Path.Combine(_directory, id);
        Directory.CreateDirectory(Path.Combine(root, ".devhub", "tasks"));
        return root;
    }

    /// <summary>写入 workspaces.json。</summary>
    /// <param name="entries">(id, root) 列表。</param>
    private void WriteWorkspaces(params (string Id, string Root)[] entries)
    {
        var payload = new
        {
            version = 1,
            workspaces = entries.Select(entry => new { id = entry.Id, root = entry.Root, enabled = true }).ToArray(),
        };

        File.WriteAllText(
            Path.Combine(_directory, "workspaces.json"),
            JsonSerializer.Serialize(payload),
            new UTF8Encoding(false));
    }

    /// <summary>写入一个任务文件。</summary>
    /// <param name="workspaceRoot">工作区根目录。</param>
    /// <param name="id">任务 id。</param>
    /// <param name="title">标题。</param>
    /// <param name="status">状态。</param>
    /// <param name="priority">优先级。</param>
    /// <param name="role">执行角色。</param>
    /// <param name="dependsOn">前置任务。</param>
    /// <param name="prompt">任务需求正文（用于验证"实质字段变化"）。</param>
    private static void WriteTask(
        string workspaceRoot,
        string id,
        string title,
        string status,
        int priority,
        string? role = null,
        string[]? dependsOn = null,
        string prompt = "执行该任务")
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["title"] = title,
            ["status"] = status,
            ["priority"] = priority,
            ["delivery"] = "review",
            ["writeScope"] = Array.Empty<string>(),
            ["prompt"] = prompt,
        };

        if (role is not null)
        {
            payload["role"] = role;
        }

        if (dependsOn is not null)
        {
            payload["dependsOn"] = dependsOn;
        }

        File.WriteAllText(
            Path.Combine(workspaceRoot, ".devhub", "tasks", $"{id}.json"),
            JsonSerializer.Serialize(payload),
            new UTF8Encoding(false));
    }

    /// <summary>统计队列项数量。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<int> CountQueueItemsAsync(CancellationToken cancellationToken) =>
        await CountRowsAsync("SELECT COUNT(*) FROM queue_item;", cancellationToken);

    /// <summary>执行一条返回单值的 COUNT 查询。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<int> CountRowsAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>插入一条待审批结果，模拟后台回合已完成但尚未决策。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="resultId">结果 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task InsertAwaitingResultAsync(string queueItemId, string resultId, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO result (id, task_id, queue_item_id, provider, outcome, summary, artifacts, created_at, read_at)
            VALUES ($id, 'T-001', $queue, 'openai', 'awaiting_review', '待审', '[]', '2026-01-01T00:00:00Z', NULL);
            UPDATE task_view SET approval_state = 'pending' WHERE id = 'T-001';
            """;
        command.Parameters.AddWithValue("$id", resultId);
        command.Parameters.AddWithValue("$queue", queueItemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>计算文件字节的 SHA-256，用于断言写入前后字节形态是否变化。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken)));

    /// <summary>读取全部 dedupe_key。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<List<string>> ReadDedupeKeysAsync(CancellationToken cancellationToken) =>
        (await ReadColumnAsync("SELECT dedupe_key FROM queue_item ORDER BY created_at, id;", cancellationToken))
        .ToList();

    /// <summary>读取单列字符串结果。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<IReadOnlyList<string>> ReadColumnAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>执行一条写语句。</summary>
    /// <param name="sql">语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
