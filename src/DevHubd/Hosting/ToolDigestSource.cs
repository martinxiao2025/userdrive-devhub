using DevHubd.Core.Queue;
using DevHubd.Core.Results;
using DevHubd.Core.Scheduling;
using DevHubd.Mcp.Abstractions;
using Microsoft.Extensions.Logging;

namespace DevHubd.Hosting;

/// <summary>
/// 工具尾部摘要（§11.4）：未读结果 / 队列积压 / 调度开关与档位。
/// <b>不包含任务标题</b>，避免把工作区内容带进每条工具输出；长度由 <see cref="ToolDigest"/> 截断。
/// </summary>
public sealed class ToolDigestSource : IToolDigestSource
{
    private readonly ResultStore _results;
    private readonly QueueService _queue;
    private readonly SchedulerStateStore _stateStore;
    private readonly ILogger<ToolDigestSource> _logger;

    /// <summary>注入结果、队列、调度状态与日志器。</summary>
    /// <param name="results">结果存取。</param>
    /// <param name="queue">队列服务。</param>
    /// <param name="stateStore">调度状态存取。</param>
    /// <param name="logger">日志器。</param>
    public ToolDigestSource(
        ResultStore results,
        QueueService queue,
        SchedulerStateStore stateStore,
        ILogger<ToolDigestSource> logger)
    {
        _results = results;
        _queue = queue;
        _stateStore = stateStore;
        _logger = logger;
    }

    /// <summary>生成一行摘要正文。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<string> BuildAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var digest = await _results.GetDigestAsync(cancellationToken).ConfigureAwait(false);
            var counters = await _queue.CountByStatusAsync(cancellationToken).ConfigureAwait(false);
            var state = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);

            var pending = counters.GetValueOrDefault("pending");
            var running = counters.GetValueOrDefault("running") + counters.GetValueOrDefault("claimed");
            var scheduler = state.Enabled
                ? "运行中"
                : $"暂停({state.PauseReason ?? "manual"})";

            return $"未读 {digest.Unread}（失败 {digest.Failures}、待复核 {digest.AwaitingReview}） · " +
                   $"队列 待派发 {pending} / 执行中 {running} · " +
                   $"调度 {scheduler} · {state.Mode}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 摘要不是主功能：记录原因后降级，不让观测查询失败连带工具不可用。
            _logger.LogWarning(exception, "工具摘要生成失败，降级为固定文案。");
            return ToolDigest.Fallback;
        }
    }
}
