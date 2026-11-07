using System.Net;
using System.Net.Sockets;

namespace DevHubd.Hosting;

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
    /// 生成端口冲突的排查提示（单行，作为结构化日志的 <c>hint</c> 字段）。
    /// </summary>
    /// <param name="port">被占用的端口。</param>
    /// <returns>含排查命令与改端口步骤的提示文本。</returns>
    public static string BuildConflictHint(int port) =>
        $"不会顺延端口：Get-NetTCPConnection -LocalPort {port} -State Listen | Select-Object OwningProcess；" +
        $"如需改端口，同步修改 plugin/mcp.json 的 url 后用 --port 启动 devhubd。";
}
