using System.Text;
using System.Text.Json;
using DevHubd.Core;
using DevHubd.Logging;
using Microsoft.Extensions.Logging;

namespace DevHubd.Hosting;

/// <summary>
/// 机器级单实例守卫：命名互斥体（同会话快速判重）+ <c>runtime.lock</c>（同用户跨会话独占）。
/// 跨用户的唯一性由 3091 端口独占绑定兜底。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\UserDrive.DevHub.Host";

    private readonly ILogger _logger;

    private Mutex? _mutex;
    private FileStream? _lockStream;

    /// <summary>注入日志器（类别为 <see cref="SingleInstanceGuard"/>）。</summary>
    /// <param name="logger">日志器。</param>
    public SingleInstanceGuard(ILogger<SingleInstanceGuard> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 尝试成为唯一实例。失败时写 Error 级日志（EventId 1002），调用方只需返回退出码 2。
    /// </summary>
    /// <param name="paths">运行时路径。</param>
    /// <param name="hint">失败时的排查提示。</param>
    /// <returns>成功获取返回 true。</returns>
    public bool TryAcquire(DevHubPaths paths, out string hint)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        if (!createdNew)
        {
            hint = BuildHint(paths.RuntimeLockPath);
            DevHubLog.InstanceAlreadyRunning(_logger, paths.RuntimeLockPath, hint);
            return false;
        }

        Directory.CreateDirectory(paths.DataDirectory);

        try
        {
            _lockStream = new FileStream(
                paths.RuntimeLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 锁文件被占用或不可写：同样是"已有实例"，但要把真实异常原因留在日志里。
            _logger.LogWarning(exception, "runtime.lock 独占打开失败，按已有实例处理：{LockPath}", paths.RuntimeLockPath);
            hint = BuildHint(paths.RuntimeLockPath);
            DevHubLog.InstanceAlreadyRunning(_logger, paths.RuntimeLockPath, hint);
            return false;
        }

        WriteLockContent(_lockStream);
        hint = string.Empty;
        return true;
    }

    /// <summary>写入 pid 与启动时间，便于排障时判断锁的持有者。</summary>
    /// <param name="stream">已独占打开的锁文件流。</param>
    private static void WriteLockContent(FileStream stream)
    {
        var payload = JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            startedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        var bytes = Encoding.UTF8.GetBytes(payload);
        stream.SetLength(0);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    /// <summary>生成"已有实例"的排查提示（单行，作为结构化日志字段）。</summary>
    /// <param name="lockPath">锁文件路径。</param>
    private static string BuildHint(string lockPath) =>
        $"查看进程：Get-Content \"{lockPath}\"; Get-Process -Name devhubd；" +
        "如需重启：先结束现有进程再启动本进程。";

    /// <summary>释放锁文件与互斥体句柄；进程退出即自动释放。</summary>
    public void Dispose()
    {
        _lockStream?.Dispose();
        _lockStream = null;

        _mutex?.Dispose();
        _mutex = null;
    }
}
