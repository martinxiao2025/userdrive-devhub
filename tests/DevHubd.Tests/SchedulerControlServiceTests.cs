using DevHubd.Core.Data;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using DevHubd.Core.Scheduling;
using DevHubd.Hosting;
using DevHubd.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 调度控制面测试：暂停 / 恢复写入 <c>scheduler_state</c> 与 <c>audit</c>，
/// <c>scan_now</c> 返回 <c>scanRequested=true</c> 并触发唤醒信号。
/// </summary>
public sealed class SchedulerControlServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteConnectionFactory _factory;
    private readonly SchedulerStateStore _stateStore;

    /// <summary>准备临时数据库与状态存取。</summary>
    public SchedulerControlServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        _stateStore = new SchedulerStateStore(_factory);
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

    /// <summary>暂停后 enabled=0 且写审计；恢复后 enabled=1 且清空暂停原因。</summary>
    [Fact]
    public async Task PauseAndResume_UpdateStateAndAuditAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = CreateControl(out _);

        var paused = await control.PauseAsync("maintenance", ct);
        Assert.False(paused.Enabled);
        Assert.Equal("maintenance", paused.PauseReason);

        var resumed = await control.ResumeAsync(ct);
        Assert.True(resumed.Enabled);
        Assert.Null(resumed.PauseReason);

        Assert.Equal(2, await CountAuditAsync(ct));
    }

    /// <summary><c>scan_now</c> 返回 scanRequested=true，并让唤醒信号完成一次等待。</summary>
    [Fact]
    public async Task RequestScan_SignalsWakeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = CreateControl(out var wake);

        var snapshot = await control.RequestScanAsync(ct);
        Assert.True(snapshot.ScanRequested);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await wake.WaitAsync(timeout.Token);
    }

    /// <summary>构造控制面实现。</summary>
    /// <param name="wake">返回新建的唤醒信号。</param>
    private SchedulerControlService CreateControl(out SchedulerWakeSignal wake)
    {
        wake = new SchedulerWakeSignal();
        return new SchedulerControlService(
            _stateStore,
            wake,
            new ProviderStateStore(_factory),
            new ModelClientRegistry(Array.Empty<IModelClient>()),
            DevHubdOptions.FromArgsAndEnvironment(Array.Empty<string>()),
            NullLogger<SchedulerControlService>.Instance);
    }

    /// <summary>统计审计行数。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<int> CountAuditAsync(CancellationToken cancellationToken)
    {
        using var connection = await _factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
