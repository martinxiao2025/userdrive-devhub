using System.Text;
using System.Text.Json;
using DevHubd.Core;
using DevHubd.Core.Data;
using DevHubd.Core.Execution;
using DevHubd.Core.Input;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using DevHubd.Core.Queue;
using DevHubd.Core.Results;
using DevHubd.Core.Roles;
using DevHubd.Core.Scheduling;
using DevHubd.Core.Tasks;
using DevHubd.Core.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// P2 执行闭环验收（设计文档 §P2 的 5 条）。全部使用 <see cref="FakeModelClient"/>，
/// 只证明编排、预算、路径与熔断语义；真实 OpenAI 调用因缺少凭据标注为未验证。
/// </summary>
public sealed class P2ExecutionTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteConnectionFactory _factory;
    private readonly WorkspaceRegistry _registry;
    private readonly QueueService _queue;
    private readonly ExecutionStore _store;
    private readonly ProviderStateStore _providerStates;
    private readonly TaskScanner _scanner;

    /// <summary>准备独立临时数据库与执行依赖。</summary>
    public P2ExecutionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _registry = new WorkspaceRegistry(Path.Combine(_directory, "workspaces.json"));
        _queue = new QueueService(_factory);
        _store = new ExecutionStore(_factory);
        _providerStates = new ProviderStateStore(_factory);
        _scanner = new TaskScanner(_factory);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }
    }

    /// <summary>
    /// 验收 1：ready 任务在一个 tick 内被派发执行，产出 awaiting_review 结果，
    /// 且日志能用 queueItemId → attempt → turnId 关联。
    /// </summary>
    [Fact]
    public async Task Pump_ReadyTask_RunsTurnAndWritesAwaitingReviewResultAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "写一份报告", "ready", 10);
        await PrepareQueueAsync(ct);

        var turnLogger = new FakeLogger<TurnRunner>();
        var coordinatorLogger = new FakeLogger<ExecutionCoordinator>();
        var fake = new FakeModelClient(
            "openai",
            (_, _, _) => Task.FromResult(TurnResult.Done("已完成报告。", 0, new TokenUsage(10, 5, false), Array.Empty<string>())));

        var coordinator = CreateCoordinator(fake, new ExecutionOptions(), turnLogger, coordinatorLogger);

        var started = await coordinator.PumpAsync(ct);
        Assert.Equal(1, started);
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1, ct);

        var outcome = await ScalarStringAsync("SELECT outcome FROM result ORDER BY created_at DESC LIMIT 1;", ct);
        Assert.Equal("awaiting_review", outcome);
        Assert.Equal("succeeded", await ScalarStringAsync("SELECT status FROM queue_item;", ct));
        Assert.Equal("succeeded", await ScalarStringAsync("SELECT outcome FROM attempt;", ct));

        var queueItemId = await ScalarStringAsync("SELECT id FROM queue_item;", ct);

        // usage 必须落在 attempt 上并可按队列项读回（真实值，非估算）。
        Assert.Equal(10L, await ScalarLongAsync("SELECT input_tokens FROM attempt;", ct));
        Assert.Equal(5L, await ScalarLongAsync("SELECT output_tokens FROM attempt;", ct));
        Assert.Equal(0L, await ScalarLongAsync("SELECT usage_estimated FROM attempt;", ct));
        Assert.Equal(1L, await ScalarLongAsync("SELECT usage_available FROM attempt;", ct));
        Assert.Equal(0L, await ScalarLongAsync("SELECT rounds_exhausted FROM attempt;", ct));

        var detail = await _queue.GetAsync(queueItemId!, ct);
        var attempt = Assert.Single(detail!.Attempts);
        Assert.Equal(10, attempt.InputTokens);
        Assert.Equal(5, attempt.OutputTokens);
        Assert.False(attempt.UsageEstimated);
        Assert.True(attempt.UsageAvailable);
        Assert.False(attempt.RoundsExhausted);
        Assert.Equal(0, attempt.ToolCalls);

        // result_list 读侧同样要带上同一份用量与工具调用次数。
        var results = await new ResultStore(_factory).ListAsync(false, 10, null, ct);
        var listed = Assert.Single(results.Items);
        Assert.Equal(10, listed.InputTokens);
        Assert.Equal(5, listed.OutputTokens);
        Assert.False(listed.UsageEstimated);
        Assert.True(listed.UsageAvailable);
        Assert.False(listed.RoundsExhausted);
        Assert.Equal(0, listed.ToolCalls);

        var turnStart = turnLogger.Collector.GetSnapshot()
            .Single(record => record.Message.Contains("模型回合开始", StringComparison.Ordinal));
        Assert.Contains(queueItemId!, turnStart.Message, StringComparison.Ordinal);
        Assert.Contains("attempt=1", turnStart.Message, StringComparison.Ordinal);
        Assert.Contains("turnId=", turnStart.Message, StringComparison.Ordinal);

        var settled = coordinatorLogger.Collector.GetSnapshot()
            .Single(record => record.Message.Contains("队列项执行成功", StringComparison.Ordinal));
        Assert.Contains(queueItemId!, settled.Message, StringComparison.Ordinal);
        Assert.Contains("turnId=", settled.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 轮次耗尽与用量不可得必须原样落库并在 result_list 可见：
    /// 状态仍是 succeeded，但 <c>roundsExhausted=true</c> 明确区别于自然完成，
    /// <c>usageAvailable=false</c> 表示 token 缺失而不是 0。
    /// </summary>
    [Fact]
    public async Task Pump_RoundsExhaustedAndUnavailableUsage_ArePersistedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "轮次耗尽", "ready", 10);
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            (_, _, _) => Task.FromResult(
                TurnResult.Done("兜底答复", 3, null, Array.Empty<string>(), roundsExhausted: true)));

        var coordinator = CreateCoordinator(fake, new ExecutionOptions());
        await coordinator.PumpAsync(ct);
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1, ct);

        Assert.Equal("succeeded", await ScalarStringAsync("SELECT status FROM queue_item;", ct));
        Assert.Equal(1L, await ScalarLongAsync("SELECT rounds_exhausted FROM attempt;", ct));
        Assert.Equal(0L, await ScalarLongAsync("SELECT usage_available FROM attempt;", ct));

        var listed = Assert.Single((await new ResultStore(_factory).ListAsync(false, 10, null, ct)).Items);
        Assert.True(listed.RoundsExhausted);
        Assert.False(listed.UsageAvailable);
        Assert.Null(listed.InputTokens);
        Assert.Null(listed.OutputTokens);
    }

    /// <summary>
    /// M1b（U12 后）：交付档位只以 <c>scheduler_state.mode</c> 为准，不看启动配置。
    /// 即使 Execution:DeliveryMode 仍是 dispatch_only，库中 mode 为 auto 且任务声明 auto，也必须 delivered。
    /// </summary>
    [Fact]
    public async Task Pump_StateModeAuto_DeliversRegardlessOfConfigAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "自动交付", "ready", 10, delivery: "auto");
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            (_, _, _) => Task.FromResult(TurnResult.Done("已完成。", 0, new TokenUsage(1, 1, false), Array.Empty<string>())));

        // 配置保持默认 dispatch_only，只把库中的 mode 设为 auto。
        var coordinator = CreateCoordinator(
            fake,
            new ExecutionOptions(),
            mode: "auto");

        await coordinator.PumpAsync(ct);
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1, ct);

        Assert.Equal("delivered", await ScalarStringAsync("SELECT outcome FROM result;", ct));
    }

    /// <summary>M1b 反向：库中 mode 为 dispatch_only 时，任务即便声明 auto 也只进 awaiting_review。</summary>
    [Fact]
    public async Task Pump_StateModeDispatchOnly_KeepsAwaitingReviewAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "待审", "ready", 10, delivery: "auto");
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            (_, _, _) => Task.FromResult(TurnResult.Done("已完成。", 0, new TokenUsage(1, 1, false), Array.Empty<string>())));

        var coordinator = CreateCoordinator(
            fake,
            new ExecutionOptions { DeliveryMode = "auto" },
            mode: "dispatch_only");

        await coordinator.PumpAsync(ct);
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1, ct);

        Assert.Equal("awaiting_review", await ScalarStringAsync("SELECT outcome FROM result;", ct));
    }

    /// <summary>M1c：任务显式 <c>requiresApproval=true</c> 时，即便全局与任务档位都是 auto 也必须待审。</summary>
    [Fact]
    public async Task Pump_TaskRequiresApproval_KeepsAwaitingReviewInAutoModeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "需要审批", "ready", 10, delivery: "auto", requiresApproval: true);
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            (_, _, _) => Task.FromResult(TurnResult.Done("已完成。", 0, new TokenUsage(1, 1, false), Array.Empty<string>())));

        var coordinator = CreateCoordinator(
            fake,
            new ExecutionOptions { DeliveryMode = "auto" },
            mode: "auto");

        await coordinator.PumpAsync(ct);
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1, ct);

        Assert.Equal("awaiting_review", await ScalarStringAsync("SELECT outcome FROM result;", ct));
        Assert.Equal("pending", await ScalarStringAsync("SELECT approval_state FROM task_view WHERE id = 'T-001';", ct));
    }

    /// <summary>
    /// U12 验收①：进程运行期间直接改库 <c>scheduler_state.mode</c>，下一回合的交付判定立即生效，
    /// 不再依赖进程内的启动种子或缓存。
    /// </summary>
    [Fact]
    public async Task Pump_ModeChangedInStoreBetweenRuns_TakesEffectAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "自动交付", "ready", 10, delivery: "auto");
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            (_, _, _) => Task.FromResult(TurnResult.Done("已完成。", 0, new TokenUsage(1, 1, false), Array.Empty<string>())));

        // 构建时库中仍是 dispatch_only；之后改库，模拟控制面在运行期切换档位。
        var coordinator = CreateCoordinator(fake, new ExecutionOptions());
        await new SchedulerStateStore(_factory).SetModeAsync("auto", ct);

        await coordinator.PumpAsync(ct);
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1, ct);

        Assert.Equal("delivered", await ScalarStringAsync("SELECT outcome FROM result;", ct));
    }

    /// <summary>
    /// U12 验收④：重启兜底把上个进程遗留的未结束 attempt 置为 <c>abandoned/PROCESS_RESTART</c>，
    /// 避免统计与排障把"进程被杀"误读成"仍在运行"。
    /// </summary>
    [Fact]
    public async Task AbandonOrphanAttempts_MarksOpenAttemptsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "重启前未完成", "ready", 10);
        await PrepareQueueAsync(ct);

        // 造一条"上个进程未收尾"的 attempt（ended_at 为空）。
        await ExecuteAsync(
            """
            INSERT INTO attempt (id, queue_item_id, attempt_no, provider, started_at)
            SELECT 'A-1', id, 1, 'openai', '2026-01-01T00:00:00Z' FROM queue_item LIMIT 1;
            """,
            ct);

        var abandoned = await _store.AbandonOrphanAttemptsAsync(DateTimeOffset.UtcNow.ToString("O"), ct);

        Assert.Equal(1, abandoned);
        Assert.Equal(
            "abandoned|PROCESS_RESTART",
            await ScalarStringAsync("SELECT outcome || '|' || error_code FROM attempt WHERE id = 'A-1';", ct));
        Assert.NotNull(await ScalarStringAsync("SELECT ended_at FROM attempt WHERE id = 'A-1';", ct));
    }

    /// <summary>
    /// 验收 2：鉴权失败按 provider 隔离——openai 进入 auth + open，vscode 状态不受影响；
    /// 且资源类失败不消耗 attempt。
    /// </summary>
    [Fact]
    public async Task Pump_AuthFailure_BreaksOnlyOpenAiAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "会被鉴权拒绝", "ready", 10);
        await PrepareQueueAsync(ct);
        await _providerStates.SaveAsync(BreakerPolicy.OnSuccess("vscode", DateTimeOffset.UtcNow), ct);

        var fake = new FakeModelClient(
            "openai",
            (_, _, _) => Task.FromResult(TurnResult.Failed(
                new ModelError(ModelErrorKinds.Auth, "AUTH_FAILED", "鉴权失败（HTTP 401）。"))));

        var coordinator = CreateCoordinator(fake, new ExecutionOptions());
        await coordinator.PumpAsync(ct);

        // 熔断状态先落库、队列回退后落库：等到两者都完成再断言，避免读到 “auth 已生效但队列仍 running” 的中间态。
        await WaitUntilAsync(
            async () => string.Equals(
                    await ScalarStringAsync("SELECT state FROM provider_state WHERE provider = 'openai';", ct),
                    "auth",
                    StringComparison.Ordinal)
                && string.Equals(
                    await ScalarStringAsync("SELECT status FROM queue_item;", ct),
                    "pending",
                    StringComparison.Ordinal),
            ct);

        var openAi = await _providerStates.GetAsync("openai", ct);
        Assert.NotNull(openAi);
        Assert.Equal(ProviderStates.Auth, openAi!.State);
        Assert.Equal(BreakerStates.Open, openAi.Breaker);

        var vsCode = await _providerStates.GetAsync("vscode", ct);
        Assert.NotNull(vsCode);
        Assert.Equal(ProviderStates.Ready, vsCode!.State);
        Assert.Equal(BreakerStates.Closed, vsCode.Breaker);

        // 资源类失败不消耗重试预算：连续 4 个 auth 回合仍可继续认领（max_attempts=3 也不会被闸门挡死）。
        var queueItemId = await ScalarStringAsync("SELECT id FROM queue_item;", ct);
        Assert.NotNull(queueItemId);
        Assert.Equal("auth", await ScalarStringAsync("SELECT error_kind FROM attempt ORDER BY attempt_no DESC LIMIT 1;", ct));
        Assert.Equal(0, await _store.CountConsumingAttemptsAsync(queueItemId!, ct));

        for (var round = 2; round <= 4; round++)
        {
            await ExecuteAsync("UPDATE queue_item SET available_at = '2000-01-01T00:00:00Z';", ct);

            // 熔断已打开时本轮只认领即释放（started=0），但 attempt 仍递增，预算豁免照样成立。
            await coordinator.PumpAsync(ct);
            var expected = (long)round;
            await WaitUntilAsync(
                async () => await ScalarLongAsync("SELECT attempt FROM queue_item;", ct) == expected
                    && string.Equals(
                        await ScalarStringAsync("SELECT status FROM queue_item;", ct),
                        "pending",
                        StringComparison.Ordinal),
                ct);
        }

        // 资源类失败不再回退 attempt：attempt 单调递增，重试预算按非 NEEDS_INPUT 且非资源类回合数判定。
        Assert.Equal("pending", await ScalarStringAsync("SELECT status FROM queue_item;", ct));
        Assert.Equal(4L, await ScalarLongAsync("SELECT attempt FROM queue_item;", ct));
        Assert.Equal(0, await _store.CountConsumingAttemptsAsync(queueItemId!, ct));
    }

    /// <summary>
    /// 验收 3：回合超过 MaxDuration 时取消，队列项置 dead、attempt 记 abandoned，
    /// 结果转 awaiting_review，且不重试（attempt 行只有 1 条）。
    /// </summary>
    [Fact]
    public async Task Pump_TurnTimeout_MarksDeadWithoutRetryAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "会超时", "ready", 10);
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return TurnResult.Done("不可达", 0, null, Array.Empty<string>());
            });

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxDurationSeconds = 1 });
        await coordinator.PumpAsync(ct);

        // 队列置 dead 后又插入失败结果，等两者都落库再断言，避免读到中间态。
        await WaitUntilAsync(
            async () => string.Equals(await ScalarStringAsync("SELECT status FROM queue_item;", ct), "dead", StringComparison.Ordinal)
                && await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1,
            ct,
            TimeSpan.FromSeconds(10));

        Assert.Equal("abandoned", await ScalarStringAsync("SELECT outcome FROM attempt;", ct));
        Assert.Equal("TURN_TIMEOUT", await ScalarStringAsync("SELECT error_code FROM attempt;", ct));
        Assert.Equal("awaiting_review", await ScalarStringAsync("SELECT outcome FROM result;", ct));
        Assert.Equal(1L, await ScalarLongAsync("SELECT COUNT(*) FROM attempt;", ct));
        Assert.Equal(1L, await ScalarLongAsync("SELECT attempt FROM queue_item;", ct));
    }

    /// <summary>
    /// 验收 5：MaxParallel=1 时两个任务严格串行；第二次 pump 才能启动第二个任务。
    /// </summary>
    [Fact]
    public async Task Pump_MaxParallelOne_SerializesTasksAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "第一个", "ready", 20);
        WriteTask(root, "T-002", "第二个", "ready", 10);
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            async (request, _, token) =>
            {
                await Task.Delay(120, token);
                return TurnResult.Done($"完成 {request.TaskId}", 0, null, Array.Empty<string>());
            });

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxParallel = 1 });

        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM queue_item WHERE status = 'succeeded';", ct) == 1, ct);
        // 队列项落终态早于并发槽位归还：等 ActiveCount 归零（此时槽位必定已释放）再发起第二次 pump，
        // 否则会偶发读到“数据库已成功但槽位仍占用”而返回 0。
        await WaitUntilAsync(() => Task.FromResult(coordinator.ActiveCount == 0), ct);
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 2, ct);

        Assert.Equal(1, fake.MaxConcurrency);
        Assert.Equal(2, fake.Calls);
    }

    /// <summary>
    /// P4 验收：停机时取消在跑回合后，队列中不残留 claimed/running 租约，队列项进入 cancelled 终态。
    /// </summary>
    [Fact]
    public async Task Shutdown_WhileRunning_LeavesNoActiveLeaseAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "长跑任务", "ready", 10);
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            async (_, _, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return TurnResult.Done("不应到达", 0, null, Array.Empty<string>());
            });

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxParallel = 1 });
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(
            async () => await CountAsync("SELECT COUNT(*) FROM queue_item WHERE status = 'running';", ct) == 1,
            ct);

        await coordinator.ShutdownAsync(ct);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM queue_item WHERE status IN ('claimed','running');", ct));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM queue_item WHERE status = 'cancelled';", ct));
    }

    /// <summary>
    /// 认领成功后 provider 状态检查抛异常时，必须归还并发槽位；
    /// 否则 MaxParallel=1 的协调器会永久失去派发能力。
    /// </summary>
    [Fact]
    public async Task Pump_WhenProviderCheckThrows_ReleasesSlotAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "第一个", "ready", 20);
        WriteTask(root, "T-002", "第二个", "ready", 10);
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            (request, _, _) => Task.FromResult(TurnResult.Done($"完成 {request.TaskId}", 0, null, Array.Empty<string>())));
        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxParallel = 1 });

        // 删掉 provider_state：第一次 Pump 在认领成功后、置 running 之前抛 SqliteException。
        var ddl = await ScalarStringAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'provider_state';",
            ct);
        Assert.NotNull(ddl);
        await ExecuteAsync("DROP TABLE provider_state;", ct);

        // 单项故障隔离：派发阶段失败不再向上抛，tick 正常返回 0。
        Assert.Equal(0, await coordinator.PumpAsync(ct));

        // 恢复表后再次 Pump：若槽位泄漏，这里会因 MaxParallel=1 而返回 0。
        // 上一项仍处于 claimed（租约未到期），本轮认领的是另一个任务。
        await ExecuteAsync(ddl!, ct);
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1, ct);
    }

    [Fact]
    public async Task Pump_WhenModelAsksInput_HoldsItemWithMonotonicAttemptAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "需要输入", "ready", 10);
        await InsertAskableRoleAsync(ct);
        await PrepareQueueAsync(ct);

        var fake = new FakeModelClient(
            "openai",
            async (request, dispatcher, token) =>
            {
                using var payload = JsonDocument.Parse(
                    """{"question":"写入哪个目录？","context":"任务未声明 writeScope","options":[{"id":"src","label":"src","recommended":true},{"id":"docs","label":"docs"}],"missingInputs":["writeScope"]}""");
                var result = await dispatcher.ExecuteAsync(request.TurnId, "ask_input", payload.RootElement, token);
                Assert.True(result.Success, result.ErrorMessage);
                return TurnResult.Done("缺少写入目录，已提问。", 1, null, Array.Empty<string>());
            });

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxParallel = 1 });
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(
            async () => await ScalarStringAsync(
                "SELECT status FROM queue_item WHERE task_id = 'T-001';",
                ct) == "pending",
            ct);

        // 挂起：pending + hold_kind=input；attempt 保持 1（单调递增，不再回退）。
        // 结算在后台任务里完成：通知在挂起之后写入，先等落库再断言，避免与结算竞态。
        await WaitUntilAsync(
            async () => await CountAsync(
                "SELECT COUNT(*) FROM notice WHERE kind = 'failure' AND title LIKE '需要输入%';",
                ct) == 1,
            ct);
        Assert.Equal(
            "pending|input|1",
            await ScalarStringAsync(
                "SELECT status || '|' || hold_kind || '|' || attempt FROM queue_item WHERE task_id = 'T-001';",
                ct));
        // 但该回合不计入重试预算：消耗预算的回合数为 0。
        var queueItemId = await ScalarStringAsync("SELECT id FROM queue_item WHERE task_id = 'T-001';", ct);
        Assert.Equal(0, await _store.CountConsumingAttemptsAsync(queueItemId!, ct));
        Assert.Equal(
            "open",
            await ScalarStringAsync("SELECT status FROM input_request WHERE task_id = 'T-001';", ct));
        Assert.Equal(
            "写入哪个目录？",
            await ScalarStringAsync("SELECT question FROM input_request WHERE task_id = 'T-001';", ct));
        Assert.Equal(
            "abandoned|needs_input",
            await ScalarStringAsync("SELECT outcome || '|' || error_kind FROM attempt;", ct));
        Assert.Equal(
            1,
            await CountAsync("SELECT COUNT(*) FROM notice WHERE kind = 'failure' AND title LIKE '需要输入%';", ct));

        // 挂起项不参与认领：再 Pump 也不会重复执行（重启后同样成立，因为 hold 在持久化层）。
        Assert.Equal(0, await coordinator.PumpAsync(ct));
        Assert.Equal(1, fake.Calls);
    }

    /// <summary>
    /// 回答 → 重新认领 → 执行 → 产生结果 的完整回路：
    /// 提问回合保留 <c>attempt_no=1</c>（abandoned/NEEDS_INPUT）与其 token；
    /// 回答清 hold 后，同一队列项以 <c>attempt_no=2</c> 重新执行并产出 awaiting_review 结果。
    /// </summary>
    [Fact]
    public async Task Pump_AfterAnswer_ResumesWithNewAttemptAndProducesResultAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "需要输入", "ready", 10);
        await InsertAskableRoleAsync(ct);
        await PrepareQueueAsync(ct);

        var calls = 0;
        var prompts = new List<string>();
        var fake = new FakeModelClient(
            "openai",
            async (request, dispatcher, token) =>
            {
                prompts.Add(request.UserPrompt);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    using var payload = JsonDocument.Parse(
                        """{"question":"写入哪个目录？","context":"任务未声明 writeScope","options":[{"id":"src","label":"src","recommended":true},{"id":"docs","label":"docs"}],"missingInputs":["writeScope"]}""");
                    var result = await dispatcher.ExecuteAsync(request.TurnId, "ask_input", payload.RootElement, token);
                    Assert.True(result.Success, result.ErrorMessage);
                    return TurnResult.Done("缺少写入目录，已提问。", 1, new TokenUsage(150, 20, false), Array.Empty<string>());
                }

                // 恢复回合必须看得到人工回答，才决定继续执行。
                Assert.Contains("## 此前的人工回答", request.UserPrompt, StringComparison.Ordinal);
                Assert.Contains("写入哪个目录？", request.UserPrompt, StringComparison.Ordinal);
                Assert.Contains("用户回答：src", request.UserPrompt, StringComparison.Ordinal);
                return TurnResult.Done("已按回答写入 src 目录。", 0, new TokenUsage(70, 30, false), Array.Empty<string>());
            });

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxParallel = 1 });

        // 第一回合：模型提问 → 队列项挂起，尚无结果。
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(
            async () => await CountAsync("SELECT COUNT(*) FROM queue_item WHERE hold_kind = 'input';", ct) == 1,
            ct);
        await WaitUntilAsync(() => Task.FromResult(coordinator.ActiveCount == 0), ct);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM result;", ct));
        Assert.Equal(1, fake.Calls);
        Assert.DoesNotContain("## 此前的人工回答", prompts[0], StringComparison.Ordinal);
        var queueItemId = await ScalarStringAsync("SELECT id FROM queue_item WHERE task_id = 'T-001';", ct);
        var requestId = await ScalarStringAsync("SELECT id FROM input_request WHERE task_id = 'T-001';", ct);
        Assert.NotNull(queueItemId);
        Assert.NotNull(requestId);
        Assert.Equal(0, await _store.CountConsumingAttemptsAsync(queueItemId!, ct));

        // 会话侧回答：清 hold，队列项回到可认领状态。
        var answered = await new InputRequestStore(_factory).AnswerAsync(
            requestId!,
            "src",
            "src",
            "tester",
            "idem-resume",
            ct);
        Assert.Equal(InputAnswerStatus.Answered, answered.Status);
        Assert.Equal(queueItemId, answered.ResumedQueueItemId);
        Assert.Equal("pending", await ScalarStringAsync("SELECT status FROM queue_item WHERE task_id = 'T-001';", ct));
        Assert.Equal(
            1,
            await CountAsync("SELECT COUNT(*) FROM queue_item WHERE task_id = 'T-001' AND hold_kind IS NULL;", ct));

        // 第二回合：重新认领（attempt_no=2）并按回答继续执行，产出结果。
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1, ct);

        Assert.Equal(2, fake.Calls);
        Assert.Equal("succeeded", await ScalarStringAsync("SELECT status FROM queue_item WHERE task_id = 'T-001';", ct));
        Assert.Equal("awaiting_review", await ScalarStringAsync("SELECT outcome FROM result;", ct));

        // attempt 只增不减：两次真实回合各占一行，编号 1、2。
        Assert.Equal(
            "1,2",
            await ScalarStringAsync(
                "SELECT group_concat(attempt_no, ',') FROM (SELECT attempt_no FROM attempt ORDER BY attempt_no);",
                ct));
        Assert.Equal(
            "abandoned|NEEDS_INPUT",
            await ScalarStringAsync(
                "SELECT outcome || '|' || error_code FROM attempt WHERE attempt_no = 1;",
                ct));
        Assert.Equal("succeeded", await ScalarStringAsync("SELECT outcome FROM attempt WHERE attempt_no = 2;", ct));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM attempt WHERE attempt_no = 2 AND error_code IS NULL;", ct));

        // token 用量按回合保留并可按队列项聚合，提问回合的消耗不丢。
        Assert.Equal(220L, await ScalarLongAsync("SELECT SUM(input_tokens) FROM attempt;", ct));
        Assert.Equal(50L, await ScalarLongAsync("SELECT SUM(output_tokens) FROM attempt;", ct));

        // 预算口径：非 NEEDS_INPUT 回合数 = 1（仅第二回合）。
        Assert.Equal(1, await _store.CountConsumingAttemptsAsync(queueItemId!, ct));

        var detail = await _queue.GetAsync(queueItemId!, ct);
        Assert.Equal(2, detail!.Attempts.Count);
    }

    /// <summary>
    /// 单项认领失败必须按项隔离：预插同一 <c>(queue_item_id, attempt_no)</c> 制造唯一冲突后，
    /// 该队列项被推迟并记录 <c>CLAIM_FAILED</c>，同一 tick 内其它队列项照常执行并产出结果。
    /// </summary>
    [Fact]
    public async Task Pump_WhenClaimFailsForOneItem_IsolatesAndContinuesOthersAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "毒化队列项", "ready", 20);
        WriteTask(root, "T-002", "正常队列项", "ready", 10);
        await PrepareQueueAsync(ct);

        var poisonedId = await ScalarStringAsync("SELECT id FROM queue_item WHERE task_id = 'T-001';", ct);
        Assert.NotNull(poisonedId);

        // 认领会把 attempt 0 → 1 并插入 attempt_no=1；预插同号行触发 UNIQUE 冲突。
        await ExecuteAsync(
            $"INSERT INTO attempt (id, queue_item_id, attempt_no, provider, started_at) VALUES ('poison', '{poisonedId}', 1, 'openai', '2026-01-01T00:00:00Z');",
            ct);

        var fake = new FakeModelClient(
            "openai",
            (request, _, _) => Task.FromResult(
                TurnResult.Done($"完成 {request.TaskId}", 0, new TokenUsage(5, 5, false), Array.Empty<string>())));

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxParallel = 2 });

        // 不抛异常、返回 1（只有 T-002 启动）：坏行不阻塞同批。
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 1, ct);

        // 毒化项：认领事务已回滚，attempt 保持 0，回到 pending 并记录 CLAIM_FAILED。
        Assert.Equal(
            "pending|CLAIM_FAILED|0",
            await ScalarStringAsync(
                "SELECT status || '|' || last_error_code || '|' || attempt FROM queue_item WHERE task_id = 'T-001';",
                ct));
        Assert.Equal(
            0,
            await CountAsync("SELECT COUNT(*) FROM attempt WHERE queue_item_id = (SELECT id FROM queue_item WHERE task_id = 'T-001') AND id <> 'poison';", ct));

        // 同批的 T-002 正常执行并产出结果。
        Assert.Equal(
            "succeeded",
            await ScalarStringAsync("SELECT status FROM queue_item WHERE task_id = 'T-002';", ct));
        Assert.Equal(
            "awaiting_review",
            await ScalarStringAsync(
                "SELECT outcome FROM result WHERE task_id = 'T-002';",
                ct));
        Assert.Equal(1, fake.Calls);
    }

    /// <summary>
    /// 角色声明的预算必须真正进入回合：maxRounds → 轮次、maxToolCalls → 工具总数、
    /// maxDurationSeconds → 墙钟；requiresApproval 即使在 auto 档位也必须让结果进入待审。
    /// </summary>
    [Fact]
    public async Task Pump_RoleBudget_IsAppliedToTurnRequestAndDeliveryAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "角色预算", "ready", 10, delivery: "auto");
        await InsertRoleAsync(
            "implementer",
            tools: """["read_file"]""",
            requiresApproval: 1,
            maxParallel: 1,
            maxRounds: 2,
            maxToolCalls: 5,
            maxDurationSeconds: 30,
            ct);

        await PrepareQueueAsync(ct);

        TurnRequest? captured = null;
        var fake = new FakeModelClient(
            "openai",
            (request, _, _) =>
            {
                captured = request;
                return Task.FromResult(TurnResult.Done("完成", 0, null, Array.Empty<string>()));
            });

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxParallel = 1 }, mode: "auto");
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(
            async () => await CountAsync("SELECT COUNT(*) FROM result WHERE task_id = 'T-001';", ct) == 1,
            ct);

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Budget.MaxToolRounds);
        Assert.Equal(5, captured.Budget.MaxToolCalls);
        Assert.Equal(TimeSpan.FromSeconds(30), captured.Budget.MaxDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), captured.Timeout);
        // 任务 delivery=auto + 全局 auto，但角色声明 requiresApproval：结果必须待审。
        Assert.Equal(
            "awaiting_review",
            await ScalarStringAsync("SELECT outcome FROM result WHERE task_id = 'T-001';", ct));
    }

    /// <summary>角色声明的 maxParallel 必须限制同角色同时运行的回合数，即使全局允许更多。</summary>
    [Fact]
    public async Task Pump_RoleMaxParallel_LimitsConcurrentTurnsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));
        WriteTask(root, "T-001", "并发 A", "ready", 10, delivery: "auto");
        WriteTask(root, "T-002", "并发 B", "ready", 10, delivery: "auto");
        await InsertRoleAsync(
            "implementer",
            tools: """["read_file"]""",
            requiresApproval: 0,
            maxParallel: 1,
            maxRounds: 10,
            maxToolCalls: 20,
            maxDurationSeconds: 300,
            ct);

        await PrepareQueueAsync(ct);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var peak = 0;
        var fake = new FakeModelClient(
            "openai",
            async (_, _, _) =>
            {
                var current = Interlocked.Increment(ref running);
                var observed = Volatile.Read(ref peak);
                while (current > observed &&
                       Interlocked.CompareExchange(ref peak, current, observed) != observed)
                {
                    observed = Volatile.Read(ref peak);
                }

                try
                {
                    await gate.Task;
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                }

                return TurnResult.Done("完成", 0, null, Array.Empty<string>());
            });

        var coordinator = CreateCoordinator(fake, new ExecutionOptions { MaxParallel = 2 }, mode: "auto");

        // 全局允许 2，但角色 maxParallel=1：第一次 pump 只能启动 1 个。
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        Assert.Equal(1, coordinator.ActiveCount);

        gate.SetResult();
        await WaitUntilAsync(() => Task.FromResult(coordinator.ActiveCount == 0), ct);

        // 前一个结束后，角色槽位释放，下一轮再启动第二个。
        Assert.Equal(1, await coordinator.PumpAsync(ct));
        await WaitUntilAsync(
            async () => await CountAsync("SELECT COUNT(*) FROM result;", ct) == 2,
            ct);

        Assert.Equal(1, peak);
        Assert.Equal(2, fake.Calls);
    }

    /// <summary>插入一条角色快照，用于验证角色声明进入回合预算与并发控制。</summary>
    /// <param name="id">角色 id。</param>
    /// <param name="tools">工具白名单 JSON。</param>
    /// <param name="requiresApproval">是否必须审批（0/1）。</param>
    /// <param name="maxParallel">角色并发上限。</param>
    /// <param name="maxRounds">回合模型轮次上限。</param>
    /// <param name="maxToolCalls">回合工具调用上限。</param>
    /// <param name="maxDurationSeconds">回合墙钟上限（秒）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task InsertRoleAsync(
        string id,
        string tools,
        int requiresApproval,
        int maxParallel,
        int maxRounds,
        int maxToolCalls,
        int maxDurationSeconds,
        CancellationToken cancellationToken) => ExecuteAsync(
        $$"""
        INSERT INTO role_view (
            id, name, description, source_path, content_hash, skills, tools, delivery,
            requires_approval, can_request_input, max_parallel, max_attempts, max_rounds,
            max_tool_calls, max_duration_seconds, max_input_tokens, payload, loaded_at, updated_at)
        VALUES (
            '{{id}}', '实现', 'desc', 'p', 'h', '[]', '{{tools}}', 'dispatch_only',
            {{requiresApproval}}, 0, {{maxParallel}}, 3, {{maxRounds}},
            {{maxToolCalls}}, {{maxDurationSeconds}}, NULL, '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
        """,
        cancellationToken);

    /// <summary>插入一个允许提问的 implementer 角色快照。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task InsertAskableRoleAsync(CancellationToken cancellationToken) => ExecuteAsync(
        """
        INSERT INTO role_view (
            id, name, description, source_path, content_hash, skills, tools, delivery,
            requires_approval, can_request_input, max_parallel, max_attempts, max_rounds,
            max_tool_calls, max_duration_seconds, max_input_tokens, payload, loaded_at, updated_at)
        VALUES (
            'implementer', '实现', 'desc', 'p', 'h', '[]', '[]', 'dispatch_only',
            0, 1, 1, 3, 10, 20, 300, NULL, '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
        """,
        cancellationToken);

    /// <summary>构建执行协调器。</summary>
    /// <param name="fake">假模型客户端。</param>
    /// <param name="options">执行配置。</param>
    /// <param name="turnLogger">回合执行器日志器。</param>
    /// <param name="coordinatorLogger">协调器日志器。</param>
    /// <param name="mode">写入 <c>scheduler_state.mode</c> 的交付档位；null 表示沿用库中现有值。</param>
    private ExecutionCoordinator CreateCoordinator(
        FakeModelClient fake,
        ExecutionOptions options,
        ILogger<TurnRunner>? turnLogger = null,
        ILogger<ExecutionCoordinator>? coordinatorLogger = null,
        string? mode = null)
    {
        var normalized = options.Normalize();
        var stateStore = new SchedulerStateStore(_factory);
        if (mode is not null)
        {
            // 交付档位以库为唯一事实来源：测试直接写库，而不是注入进程内缓存。
            stateStore.SetModeAsync(mode, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        var roleSnapshots = new RoleSnapshotStore(new DevHubDbContextFactory(_factory.ConnectionString));
        var runner = new TurnRunner(
            new ModelClientRegistry(new[] { fake }),
            normalized,
            stateStore,
            new DevHubPaths(_directory),
            new InputRequestStore(_factory),
            roleSnapshots,
            NullLoggerFactory.Instance,
            turnLogger ?? NullLogger<TurnRunner>.Instance);

        return new ExecutionCoordinator(
            _queue,
            _store,
            _providerStates,
            _registry,
            runner,
            normalized,
            coordinatorLogger ?? NullLogger<ExecutionCoordinator>.Instance,
            roleSnapshots);
    }

    /// <summary>扫描工作区并入队全部 ready 任务。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task PrepareQueueAsync(CancellationToken cancellationToken)
    {
        var loaded = await _registry.LoadAsync(cancellationToken);
        foreach (var workspace in loaded.Workspaces)
        {
            await _scanner.ScanAsync(workspace, cancellationToken);
            await _queue.EnqueueReadyTasksAsync(workspace.Id, "openai", dryRun: false, cancellationToken);
        }
    }

    /// <summary>创建工作区根目录（含 .devhub/tasks）。</summary>
    /// <param name="id">工作区 id。</param>
    private string CreateWorkspace(string id)
    {
        var root = Path.Combine(_directory, id);
        Directory.CreateDirectory(Path.Combine(root, ".devhub", "tasks"));
        return root;
    }

    /// <summary>写入 workspaces.json。</summary>
    /// <param name="entries">工作区条目。</param>
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
    private static void WriteTask(
        string workspaceRoot,
        string id,
        string title,
        string status,
        int priority,
        string delivery = "review",
        bool requiresApproval = false)
    {
        var payload = new
        {
            id,
            title,
            status,
            priority,
            delivery,
            requiresApproval,
            writeScope = Array.Empty<string>(),
            prompt = "执行该任务",
        };

        File.WriteAllText(
            Path.Combine(workspaceRoot, ".devhub", "tasks", $"{id}.json"),
            JsonSerializer.Serialize(payload),
            new UTF8Encoding(false));
    }

    /// <summary>轮询等待条件成立。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="timeout">超时；默认 5 秒。</param>
    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        Assert.Fail("等待条件超时。");
    }

    /// <summary>统计行数。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<long> CountAsync(string sql, CancellationToken cancellationToken) =>
        await ScalarLongAsync(sql, cancellationToken);

    /// <summary>读取整数值。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<long> ScalarLongAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>执行一条非查询 SQL（测试内构造异常/恢复 schema 用）。</summary>
    /// <param name="sql">SQL 语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>读取字符串值。</summary>
    /// <param name="sql">查询语句。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<string?> ScalarStringAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
