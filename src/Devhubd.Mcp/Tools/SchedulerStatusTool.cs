using System.ComponentModel;
using System.Text.Json;
using Devhubd.Mcp.Abstractions;
using Devhubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Devhubd.Mcp.Tools;

/// <summary>
/// <c>scheduler_status</c>：向 Chat 暴露调度器当前状态（P0 为骨架数据）。
/// </summary>
[McpServerToolType]
public sealed class SchedulerStatusTool
{
    private readonly ISchedulerStatusSource _source;

    /// <summary>注入状态数据源（由宿主提供实现）。</summary>
    /// <param name="source">状态数据源。</param>
    public SchedulerStatusTool(ISchedulerStatusSource source)
    {
        _source = source;
    }

    /// <summary>
    /// 返回调度器、队列、provider、MCP 端点与宿主进程的状态快照。
    /// </summary>
    /// <param name="verbose">预留：是否返回更详细信息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "scheduler_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("查看定时调度器状态：是否启用、档位、扫描间隔、队列计数、provider 健康、MCP 端口与宿主进程信息。")]
    public async Task<CallToolResult> GetStatusAsync(
        [Description("预留参数；为 true 时要求更详细的信息。")] bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await _source.GetSnapshotAsync(verbose, cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(snapshot, ToolJson.Options);

            return new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = json } },
            };
        }
        catch (Exception ex)
        {
            return McpErrors.Envelope(
                code: "SCHEDULER_UNAVAILABLE",
                message: "调度器状态不可用：数据库或宿主尚未就绪。",
                retryable: true,
                next: "稍后重试；若持续失败，查看 devhubd 日志与数据目录权限。",
                details: new { error = ex.GetType().Name });
        }
    }
}
