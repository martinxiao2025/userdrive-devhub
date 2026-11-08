using System.ComponentModel;
using System.Text.Json;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary><c>scheduler_status</c>：向 Chat 暴露调度器当前状态。</summary>
[McpServerToolType]
public sealed class SchedulerStatusTool
{
    private readonly ISchedulerStatusSource _source;
    private readonly IToolDigestSource _digest;

    /// <summary>注入状态数据源与摘要源。</summary>
    /// <param name="source">状态数据源。</param>
    /// <param name="digest">摘要数据源。</param>
    public SchedulerStatusTool(ISchedulerStatusSource source, IToolDigestSource digest)
    {
        _source = source;
        _digest = digest;
    }

    /// <summary>
    /// 返回调度器、队列、provider、MCP 端点与宿主进程的状态快照。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "scheduler_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("查看定时调度器状态：是否启用、档位、扫描间隔、队列计数、provider 健康、MCP 端口与宿主进程信息。")]
    public async Task<CallToolResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await _source.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(snapshot, ToolJson.Options);
            return await ToolResults.SuccessAsync(json, _digest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    code: "SCHEDULER_UNAVAILABLE",
                    message: "调度器状态不可用：数据库或宿主尚未就绪。",
                    retryable: true,
                    next: "稍后重试；若持续失败，查看 devhubd 日志与数据目录权限。",
                    details: new { error = ex.GetType().Name }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
