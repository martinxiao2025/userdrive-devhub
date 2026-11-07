using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DevHubd.Hosting;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 启动失败路径的真实复现：直接拉起宿主进程，断言退出码与关键日志。
/// 覆盖退出码 2（单实例互斥）、3（端口占用）、4（数据目录不可写）、5（迁移失败）。
/// 测试项目引用了宿主项目，apphost（devhubd.exe）会随依赖复制到测试输出目录。
/// 与 <see cref="LoggingTests"/> 共用机器级互斥体，必须放在同一 collection 串行执行。
/// </summary>
[Collection("single-instance")]
public sealed class StartupExitCodeTests : IDisposable
{
    private const string HostMutexName = @"Local\UserDrive.DevHub.Host";

    private readonly List<string> _directories = new();

    /// <summary>清理本用例创建的临时目录。</summary>
    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 忽略：临时目录由系统回收。
            }
        }
    }

    /// <summary>退出码 2：互斥体已被持有（等价于同机已有实例）时，新进程立即失败退出。</summary>
    [Fact]
    public async Task Start_WhenInstanceMutexHeld_ExitsWith2Async()
    {
        var ct = TestContext.Current.CancellationToken;
        using var mutex = TryHoldMutex();
        if (mutex is null)
        {
            Assert.Skip("同机已有 devhubd 实例持有单实例互斥体，无法复现退出码 2。");
        }

        var result = await RunHostAsync(CreateDirectory(), Array.Empty<string>(), ct);

        Assert.Equal(ExitCodes.InstanceAlreadyRunning, result.ExitCode);
        Assert.Contains("已有实例在运行", result.Output, StringComparison.Ordinal);
        Assert.Contains("exitCode=2", result.Output, StringComparison.Ordinal);
    }

    /// <summary>退出码 3：端口已被占用时失败退出，不顺延到其它端口。</summary>
    [Fact]
    public async Task Start_WhenPortOccupied_ExitsWith3Async()
    {
        var ct = TestContext.Current.CancellationToken;
        EnsureMutexFree();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var arguments = new[] { "--port", port.ToString(CultureInfo.InvariantCulture) };
        var result = await RunHostAsync(CreateDirectory(), arguments, ct);

        Assert.Equal(ExitCodes.PortInUse, result.ExitCode);
        Assert.Contains("已被占用", result.Output, StringComparison.Ordinal);
        Assert.Contains("exitCode=3", result.Output, StringComparison.Ordinal);
    }

    /// <summary>退出码 4：数据目录路径不可创建（父路径是普通文件）时失败退出。</summary>
    [Fact]
    public async Task Start_WhenDataDirectoryNotWritable_ExitsWith4Async()
    {
        var ct = TestContext.Current.CancellationToken;
        EnsureMutexFree();

        var root = CreateDirectory();
        var blocker = Path.Combine(root, "blocker");
        await File.WriteAllTextAsync(blocker, "x", ct);

        var result = await RunHostAsync(Path.Combine(blocker, "data"), Array.Empty<string>(), ct);

        Assert.Equal(ExitCodes.DataDirectoryNotWritable, result.ExitCode);
        Assert.Contains("数据目录不可写", result.Output, StringComparison.Ordinal);
        Assert.Contains("exitCode=4", result.Output, StringComparison.Ordinal);
    }

    /// <summary>退出码 5：数据库文件损坏导致迁移失败时失败退出。</summary>
    [Fact]
    public async Task Start_WhenDatabaseCorrupted_ExitsWith5Async()
    {
        var ct = TestContext.Current.CancellationToken;
        EnsureMutexFree();

        var directory = CreateDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory, "devhub.db"), "not a sqlite database", ct);

        var result = await RunHostAsync(directory, Array.Empty<string>(), ct);

        Assert.Equal(ExitCodes.MigrationFailed, result.ExitCode);
        Assert.Contains("数据库迁移失败", result.Output, StringComparison.Ordinal);
        Assert.Contains("exitCode=5", result.Output, StringComparison.Ordinal);
    }

    /// <summary>宿主可执行文件路径（测试输出目录内的 apphost）。</summary>
    private static string HostExecutable =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "devhubd.exe" : "devhubd");

    /// <summary>创建并登记一个临时目录。</summary>
    /// <returns>临时目录绝对路径。</returns>
    private string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "devhubd-exit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    /// <summary>尝试成为互斥体持有者；已被其它进程占用时返回 null。</summary>
    /// <returns>持有中的互斥体，或 null。</returns>
    private static Mutex? TryHoldMutex()
    {
        var mutex = new Mutex(initiallyOwned: true, HostMutexName, out var createdNew);
        if (createdNew)
        {
            return mutex;
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>确认机器级互斥体空闲；否则跳过，避免把"已有实例"误判成其它失败。</summary>
    private static void EnsureMutexFree()
    {
        using var mutex = TryHoldMutex();
        if (mutex is null)
        {
            Assert.Skip("同机已有 devhubd 实例持有单实例互斥体，跳过启动失败路径复现。");
        }
    }

    /// <summary>启动宿主进程并等待退出，收集标准输出与错误输出。</summary>
    /// <param name="dataDirectory">数据目录。</param>
    /// <param name="extraArguments">额外命令行参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>退出码与合并后的日志文本。</returns>
    private static async Task<HostRunResult> RunHostAsync(
        string dataDirectory,
        IReadOnlyList<string> extraArguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(HostExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        foreach (var argument in extraArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // 失败路径与凭据无关：移除 key 避免探测干扰；关闭调度避免后台扫描。
        startInfo.Environment.Remove("DEVHUB_OPENAI_API_KEY");
        startInfo.Environment["DEVHUB_SCHEDULER"] = "off";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 devhubd 进程。");
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        var output = (await standardOutput) + (await standardError);
        return new HostRunResult(process.ExitCode, output);
    }

    /// <summary>一次失败启动的结果。</summary>
    /// <param name="ExitCode">进程退出码。</param>
    /// <param name="Output">合并后的日志文本。</param>
    private sealed record HostRunResult(int ExitCode, string Output);
}
