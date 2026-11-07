using System.Net;
using System.Net.Sockets;
using Devhubd.Hosting;
using Xunit;

namespace Devhubd.Tests;

/// <summary>
/// 端口守卫测试：锁定"占用即失败、不顺延"的语义与错误文案要素。
/// </summary>
public sealed class PortGuardTests
{
    /// <summary>空闲端口应判定为可用。</summary>
    [Fact]
    public void IsAvailable_ReturnsTrueForFreePort()
    {
        var port = FindFreePort();
        Assert.True(PortGuard.IsAvailable("127.0.0.1", port));
    }

    /// <summary>已被监听的回环端口应判定为不可用（即启动必须失败退出）。</summary>
    [Fact]
    public void IsAvailable_ReturnsFalseWhenPortIsListening()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.False(PortGuard.IsAvailable("127.0.0.1", port));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>错误文案必须含端口、占用结论、排查命令与"不顺延"说明。</summary>
    [Fact]
    public void ConflictMessage_ContainsPortAndDiagnostics()
    {
        var message = PortGuard.BuildConflictMessage(3091);

        Assert.Contains("127.0.0.1:3091 已被占用", message);
        Assert.Contains("Get-NetTCPConnection -LocalPort 3091", message);
        Assert.Contains("不会顺延端口", message);
        Assert.Contains("plugin/mcp.json", message);
    }

    /// <summary>让操作系统分配一个当前空闲的回环端口。</summary>
    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
