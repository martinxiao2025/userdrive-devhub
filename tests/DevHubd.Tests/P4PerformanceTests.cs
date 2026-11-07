using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Queue;
using DevHubd.Core.Scheduling;
using DevHubd.Core.Tasks;
using DevHubd.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// P4 性能基线：1000 个任务文件时一次 reconcile ≤ 500 ms，<c>queue_list</c> 一页（50 条）≤ 50 ms。
/// 计时只覆盖被测操作，不含测试数据写入文件的时间。
/// </summary>
public sealed class P4PerformanceTests : IDisposable
{
    private const int TaskCount = 1000;

    private readonly string _directory;
    private readonly SqliteConnectionFactory _factory;
    private readonly QueueService _queue;
    private readonly SchedulerEngine _engine;

    /// <summary>注入测试输出，便于在测试报告中记录实测值。</summary>
    /// <param name="output">xUnit 输出器。</param>
    public P4PerformanceTests(ITestOutputHelper output)
    {
        Output = output;
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        var registry = new WorkspaceRegistry(Path.Combine(_directory, "workspaces.json"));
        _queue = new QueueService(_factory);
        _engine = new SchedulerEngine(
            registry,
            new TaskScanner(_factory),
            _queue,
            new SchedulerStateStore(_factory),
            NullLogger<SchedulerEngine>.Instance);

        PrepareWorkspace();
    }

    /// <summary>测试输出器。</summary>
    private ITestOutputHelper Output { get; }

    /// <summary>清理临时目录。</summary>
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

    /// <summary>1000 个任务文件：扫描 + 入队（reconcile）不超过 500 ms。</summary>
    [Fact]
    public async Task TickAsync_WithThousandTasks_CompletesWithinBaselineAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        var stopwatch = Stopwatch.StartNew();
        var result = await _engine.TickAsync(ct);
        stopwatch.Stop();

        Output.WriteLine($"reconcile(1000 tasks) = {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(TaskCount, result.Scanned);
        Assert.Equal(TaskCount, result.Enqueued);
        Assert.True(
            stopwatch.Elapsed <= TimeSpan.FromMilliseconds(500),
            $"reconcile 耗时 {stopwatch.Elapsed.TotalMilliseconds:F1} ms，超过 500 ms 基线。");

        // 稳态重复扫描同样在基线内（验证 MarkMissing 的 1000 参数路径不吃掉预算）。
        var steady = Stopwatch.StartNew();
        var second = await _engine.TickAsync(ct);
        steady.Stop();
        Output.WriteLine($"reconcile(1000 tasks, steady state) = {steady.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(TaskCount, second.Scanned);
        Assert.Equal(0, second.Enqueued);
        Assert.True(
            steady.Elapsed <= TimeSpan.FromMilliseconds(500),
            $"稳态 reconcile 耗时 {steady.Elapsed.TotalMilliseconds:F1} ms，超过 500 ms 基线。");
    }

    /// <summary>队列 1000 项时，queue_list 一页（50 条）不超过 50 ms。</summary>
    [Fact]
    public async Task ListAsync_WithThousandQueueItems_ReturnsPageWithinBaselineAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _engine.TickAsync(ct);

        var stopwatch = Stopwatch.StartNew();
        var page = await _queue.ListAsync("all", 50, null, ct);
        stopwatch.Stop();

        Output.WriteLine($"queue_list page(50/1000) = {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(50, page.Items.Count);
        Assert.NotNull(page.NextCursor);
        Assert.True(
            stopwatch.Elapsed <= TimeSpan.FromMilliseconds(50),
            $"queue_list 一页耗时 {stopwatch.Elapsed.TotalMilliseconds:F1} ms，超过 50 ms 基线。");
    }

    /// <summary>建立工作区并写入 1000 个任务文件。</summary>
    private void PrepareWorkspace()
    {
        var root = Path.Combine(_directory, "alpha");
        var taskDirectory = Path.Combine(root, ".devhub", "tasks");
        Directory.CreateDirectory(taskDirectory);

        var manifest = new
        {
            version = 1,
            workspaces = new[] { new { id = "alpha", root, enabled = true } },
        };
        File.WriteAllText(
            Path.Combine(_directory, "workspaces.json"),
            JsonSerializer.Serialize(manifest),
            new UTF8Encoding(false));

        for (var index = 0; index < TaskCount; index++)
        {
            var payload = new
            {
                id = $"T-{index:D4}",
                title = $"性能基线任务 {index:D4}",
                status = "ready",
                priority = index % 10,
                delivery = "review",
                writeScope = Array.Empty<string>(),
                prompt = "执行该任务",
            };

            File.WriteAllText(
                Path.Combine(taskDirectory, $"T-{index:D4}.json"),
                JsonSerializer.Serialize(payload),
                new UTF8Encoding(false));
        }
    }
}
