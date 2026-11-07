using DevHubd.Mcp.Contracts;

namespace DevHubd.Mcp.Abstractions;

/// <summary>
/// 调度状态数据源。由宿主（DevHubd）实现，MCP 工具层只负责整形与错误包装，
/// 以保持依赖方向 DevHubd → DevHubd.Mcp → DevHubd.Core。
/// </summary>
public interface ISchedulerStatusSource
{
    /// <summary>
    /// 读取当前调度器状态快照。
    /// </summary>
    /// <param name="verbose">是否要求更详细的信息（P0 暂不影响返回内容）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<SchedulerStatusSnapshot> GetSnapshotAsync(bool verbose, CancellationToken cancellationToken = default);
}
