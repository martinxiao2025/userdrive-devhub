using System.Text;
using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using DevHubd.Core.Queue;
using DevHubd.Core.Scheduling;
using DevHubd.Core.Workspaces;
using DevHubd.Hosting;
using DevHubd.Mcp.Abstractions;
using DevHubd.Queue;
using DevHubd.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// P3 控制面：档位锁定、熔断重置、provider 探测，以及"显式 provider 不可用时拒绝派发"。
/// </summary>
public sealed class P3ControlTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "devhubd-tests",
        Guid.NewGuid().ToString("N"));

    private SqliteConnectionFactory _factory = null!;
    private SchedulerStateStore _stateStore = null!;
    private ProviderStateStore _providerStates = null!;

    /// <summary>准备临时数据库。</summary>
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken);
        _stateStore = new SchedulerStateStore(_factory);
        _providerStates = new ProviderStateStore(_factory);
    }

    /// <summary>清理临时目录。</summary>
    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>上报的 deliveryMode 必须等于实际执行配置，而不是命令行解锁标记。</summary>
    [Fact]
    public async Task Limits_DeliveryMode_MatchesExecutionConfigAsync()
    {
        var options = DevHubdOptions.FromArgsAndEnvironment(Array.Empty<string>());
        Assert.False(options.AllowAutoDelivery);

        var execution = new ExecutionOptions { DeliveryMode = "auto" }.Normalize();
        var limits = await new DevHubd.Endpoints.LimitsSource(execution, options)
            .GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("auto", limits.Mode);
    }

    /// <summary>未解锁 auto 时 set_mode auto 返回 MODE_LOCKED，且数据库档位不变。</summary>
    [Fact]
    public async Task SetMode_AutoWithoutUnlock_ReturnsModeLockedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = CreateControl(new StubModelClient("openai", ready: true));

        var exception = await Assert.ThrowsAsync<SchedulerControlException>(() => control.SetModeAsync("auto", ct));
        Assert.Equal("MODE_LOCKED", exception.Code);
        Assert.Equal("dispatch_only", (await _stateStore.ReadAsync(ct)).Mode);
    }

    /// <summary>解锁后可以切到 auto，并写审计。</summary>
    [Fact]
    public async Task SetMode_AutoWithUnlock_PersistsAndAuditsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = CreateControl(
            new IModelClient[] { new StubModelClient("openai", ready: true) },
            DevHubdOptions.FromArgsAndEnvironment(new[] { "--allow-auto-delivery" }));

        var snapshot = await control.SetModeAsync("auto", ct);

        Assert.True(snapshot.Changed);
        Assert.Equal("auto", snapshot.Mode);
        Assert.Equal("auto", (await _stateStore.ReadAsync(ct)).Mode);
        Assert.Equal(1, await CountAuditAsync("scheduler.set_mode", ct));
    }

    /// <summary>熔断未打开时重置返回 NOT_PAUSED。</summary>
    [Fact]
    public async Task ResetBreaker_WhenClosed_ReturnsNotPausedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = CreateControl(new StubModelClient("openai", ready: true));
        await _providerStates.SaveAsync(
            new ProviderStateRecord(
                "openai",
                ProviderStates.Ready,
                null,
                BreakerStates.Closed,
                null,
                null,
                0,
                DateTimeOffset.UtcNow.ToString("O")),
            ct);

        var exception = await Assert.ThrowsAsync<SchedulerControlException>(
            () => control.ResetBreakerAsync("openai", null, ct));
        Assert.Equal("NOT_PAUSED", exception.Code);
    }

    /// <summary>熔断打开后可人工重置为 closed，并清零连续失败。</summary>
    [Fact]
    public async Task ResetBreaker_WhenOpen_ClosesBreakerAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = CreateControl(new StubModelClient("openai", ready: true));
        await _providerStates.SaveAsync(
            new ProviderStateRecord(
                "openai",
                ProviderStates.Ready,
                null,
                BreakerStates.Open,
                "quota",
                "2026-01-01T00:00:00Z",
                5,
                DateTimeOffset.UtcNow.ToString("O")),
            ct);

        var snapshot = await control.ResetBreakerAsync("openai", "人工确认", ct);
        var record = await _providerStates.GetAsync("openai", ct);

        Assert.True(snapshot.Changed);
        Assert.NotNull(record);
        Assert.Equal(BreakerStates.Closed, record!.Breaker);
        Assert.Equal(0, record.ConsecutiveFailures);
        Assert.Equal(1, await CountAuditAsync("provider.reset_breaker", ct));
    }

    /// <summary>probe_models 会把两个 provider 的探测结果落库。</summary>
    [Fact]
    public async Task ProbeModels_PersistsProviderStatesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = CreateControl(
            new StubModelClient("openai", ready: true),
            new StubModelClient("vscode", ready: false));

        var snapshot = await control.ProbeModelsAsync(ct);

        var openai = await _providerStates.GetAsync("openai", ct);
        var vscode = await _providerStates.GetAsync("vscode", ct);
        Assert.Equal(ProviderStates.Ready, openai!.State);
        Assert.Equal(ProviderStates.Down, vscode!.State);
        Assert.Equal(2, snapshot.Providers.Count);
    }

    /// <summary>显式指定 vscode 且桥不在线时，dispatch_now 语义返回 PROVIDER_UNAVAILABLE。</summary>
    [Fact]
    public async Task DispatchNow_VsCodeWithoutBridge_ReturnsProviderUnavailableAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspaceRoot = Path.Combine(_directory, "alpha");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, ".devhub", "tasks"));
        File.WriteAllText(
            Path.Combine(workspaceRoot, ".devhub", "tasks", "T-001.json"),
            JsonSerializer.Serialize(new
            {
                id = "T-001",
                title = "任务",
                status = "ready",
                priority = 10,
                delivery = "review",
                writeScope = Array.Empty<string>(),
                prompt = "执行",
            }),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(_directory, "workspaces.json"),
            JsonSerializer.Serialize(new
            {
                version = 1,
                workspaces = new[] { new { id = "alpha", root = workspaceRoot, enabled = true } },
            }),
            new UTF8Encoding(false));

        var registry = new WorkspaceRegistry(Path.Combine(_directory, "workspaces.json"));
        var queue = new QueueService(_factory);
        var dispatcher = new DispatchService(registry, queue, _stateStore, _providerStates);

        // 先让任务进入 task_view（否则派发会先返回 UNKNOWN_TASK）。
        await new Core.Tasks.TaskScanner(_factory)
            .ScanAsync(new WorkspaceDefinition("alpha", workspaceRoot, true), ct);

        var outcome = await dispatcher.DispatchNowAsync("T-001", "alpha", "vscode", dryRun: false, force: false, ct);

        Assert.Equal(DispatchStatus.ProviderUnavailable, outcome.Status);
        Assert.Equal("vscode", outcome.Provider);
    }

    /// <summary>构造控制面；可变参数依次追加到注册表。</summary>
    /// <param name="clients">模型客户端。</param>
    private SchedulerControlService CreateControl(params IModelClient[] clients) =>
        CreateControl(clients, DevHubdOptions.FromArgsAndEnvironment(Array.Empty<string>()));

    /// <summary>构造控制面并指定启动选项。</summary>
    /// <param name="clients">模型客户端数组（需展开）。</param>
    /// <param name="options">启动选项。</param>
    private SchedulerControlService CreateControl(IModelClient[] clients, DevHubdOptions options) =>
        new(
            _stateStore,
            new SchedulerWakeSignal(),
            _providerStates,
            new ModelClientRegistry(clients),
            options,
            NullLogger<SchedulerControlService>.Instance);

    /// <summary>统计指定动作的审计条数。</summary>
    /// <param name="action">动作名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<int> CountAuditAsync(string action, CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit WHERE action = $action;";
        command.Parameters.AddWithValue("$action", action);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>不触网的最小模型客户端。</summary>
    private sealed class StubModelClient : IModelClient
    {
        private readonly bool _ready;

        /// <summary>构造桩客户端。</summary>
        /// <param name="provider">provider 标识。</param>
        /// <param name="ready">探测结果是否可用。</param>
        public StubModelClient(string provider, bool ready)
        {
            Provider = provider;
            _ready = ready;
        }

        /// <summary>provider 标识。</summary>
        public string Provider { get; }

        /// <summary>返回预设的探测结果。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        public Task<ModelHealth> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_ready
                ? new ModelHealth(true, ProviderStates.Ready, null, "stub-model")
                : new ModelHealth(false, ProviderStates.Down, "stub down", null));

        /// <summary>本用例不会真正执行回合。</summary>
        public Task<TurnResult> RunTurnAsync(
            TurnRequest request,
            IToolDispatcher dispatcher,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("桩客户端不执行回合。");
    }
}
