using System.Text;
using System.Text.Json;
using Devhubd.Core;

namespace Devhubd.Hosting;

/// <summary>
/// 机器级单实例守卫：命名互斥体（同会话快速判重）+ <c>runtime.lock</c>（同用户跨会话独占）。
/// 跨用户的唯一性由 3091 端口独占绑定兜底。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\UserDrive.DevHub.Host";

    private Mutex? _mutex;
    private FileStream? _lockStream;

    /// <summary>
    /// 尝试成为唯一实例。
    /// </summary>
    /// <param name="paths">运行时路径。</param>
    /// <param name="error">失败时的可读原因。</param>
    /// <returns>成功获取返回 true。</returns>
    public bool TryAcquire(DevhubPaths paths, out string error)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        if (!createdNew)
        {
            error = BuildInstanceMessage(paths.RuntimeLockPath);
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
        catch (IOException)
        {
            error = BuildInstanceMessage(paths.RuntimeLockPath);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = BuildInstanceMessage(paths.RuntimeLockPath);
            return false;
        }

        WriteLockContent(_lockStream);
        error = string.Empty;
        return true;
    }

    /// <summary>写入 pid 与启动时间，便于排障时判断锁的持有者。</summary>
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

    /// <summary>生成"已有实例"的可诊断提示。</summary>
    private static string BuildInstanceMessage(string lockPath) =>
        $"""
        [devhubd] 启动失败：已有实例在运行（机器级单实例）。
        锁文件：{lockPath}
        查看进程：Get-Content "{lockPath}"; Get-Process -Name devhubd
        如需重启：先结束现有进程（Get-Process -Name devhubd | Stop-Process），再启动本进程。
        """;

    /// <summary>释放锁文件与互斥体句柄；进程退出即自动释放。</summary>
    public void Dispose()
    {
        _lockStream?.Dispose();
        _lockStream = null;

        _mutex?.Dispose();
        _mutex = null;
    }
}
