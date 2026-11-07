using System.Net;
using System.Net.Sockets;

namespace Devhubd.Hosting;

/// <summary>
/// 端口守卫：启动前做独占绑定探测。端口被占用时**失败退出**（退出码 3），不顺延到 3092/3093——
/// 顺延会让静态的 <c>plugin/mcp.json</c> 指向不存在的端口，把"启动即报错"退化成"运行期静默不可用"。
/// </summary>
public static class PortGuard
{
    /// <summary>
    /// 检测回环端口是否可用。
    /// </summary>
    /// <param name="bindAddress">绑定地址，默认 127.0.0.1。</param>
    /// <param name="port">待检测端口。</param>
    /// <returns>可独占绑定返回 true。</returns>
    public static bool IsAvailable(string bindAddress, int port)
    {
        var address = IPAddress.Parse(bindAddress);
        var listener = new TcpListener(address, port);

        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
                // 已停止或未启动，忽略。
            }
        }
    }

    /// <summary>
    /// 生成端口占用的可诊断错误文案（含排查命令与改端口步骤）。
    /// </summary>
    /// <param name="port">被占用的端口。</param>
    public static string BuildConflictMessage(int port) =>
        $"""
        [devhubd] 启动失败：127.0.0.1:{port} 已被占用，进程退出（不会顺延端口）。
        原因：插件包 plugin/mcp.json 的 URL 固定指向 127.0.0.1:{port}，顺延会让 Chat 连到不存在的端口。
        排查占用进程：
          Get-NetTCPConnection -LocalPort {port} -State Listen | Select-Object OwningProcess
          Get-Process -Id <上一步的 OwningProcess>
        如需改端口：同步修改 plugin/mcp.json 的 url 与重装插件，然后用 --port <新端口> 启动 devhubd。
        """;
}
