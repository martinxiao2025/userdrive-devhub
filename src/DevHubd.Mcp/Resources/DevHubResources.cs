using System.ComponentModel;
using System.Text.Json;
using DevHubd.Mcp.Abstractions;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Resources;

/// <summary>
/// 只读 MCP 资源（§11.2）：全部返回 <c>application/json</c> 文本，不产生副作用。
/// 资源异常统一返回 <c>{"error": ...}</c>，避免把宿主异常直接抛给 Chat。
/// </summary>
[McpServerResourceType]
public sealed class DevHubResources
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ISchedulerStatusSource _status;
    private readonly IQueueQuerySource _queue;
    private readonly IResultQuerySource _results;
    private readonly ILimitsSource _limits;

    /// <summary>注入四类只读数据源。</summary>
    /// <param name="status">调度状态。</param>
    /// <param name="queue">队列查询。</param>
    /// <param name="results">结果查询。</param>
    /// <param name="limits">运行期上限。</param>
    public DevHubResources(
        ISchedulerStatusSource status,
        IQueueQuerySource queue,
        IResultQuerySource results,
        ILimitsSource limits)
    {
        _status = status;
        _queue = queue;
        _results = results;
        _limits = limits;
    }

    /// <summary>调度器状态快照。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerResource(
        UriTemplate = "devhub://scheduler/status",
        Name = "scheduler-status",
        Title = "调度器状态",
        MimeType = "application/json")]
    [Description("调度器、队列、provider 与宿主进程的完整状态快照。")]
    public async Task<string> SchedulerStatusAsync(CancellationToken cancellationToken = default) =>
        await SafeAsync(async () => JsonSerializer.Serialize(
            await _status.GetSnapshotAsync(false, cancellationToken).ConfigureAwait(false), _json)).ConfigureAwait(false);

    /// <summary>待派发与执行中的队列项（最多 100 条）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerResource(
        UriTemplate = "devhub://scheduler/queue",
        Name = "scheduler-queue",
        Title = "活跃队列项",
        MimeType = "application/json")]
    [Description("待派发、已认领与执行中的队列项快照。")]
    public async Task<string> QueueAsync(CancellationToken cancellationToken = default) =>
        await SafeAsync(async () => JsonSerializer.Serialize(
            await _queue.ListActiveAsync(100, cancellationToken).ConfigureAwait(false), _json)).ConfigureAwait(false);

    /// <summary>未读结果（最多 20 条）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerResource(
        UriTemplate = "devhub://results/unread",
        Name = "results-unread",
        Title = "未读结果",
        MimeType = "application/json")]
    [Description("最近 20 条未读结果及其摘要。")]
    public async Task<string> UnreadResultsAsync(CancellationToken cancellationToken = default) =>
        await SafeAsync(async () => JsonSerializer.Serialize(
            await _results.ListAsync(true, 20, null, cancellationToken).ConfigureAwait(false), _json)).ConfigureAwait(false);

    /// <summary>任务物化视图（最多 200 条）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerResource(
        UriTemplate = "devhub://tasks",
        Name = "tasks",
        Title = "任务清单",
        MimeType = "application/json")]
    [Description("已扫描工作区的任务清单（id / 标题 / 状态 / 优先级）。")]
    public async Task<string> TasksAsync(CancellationToken cancellationToken = default) =>
        await SafeAsync(async () => JsonSerializer.Serialize(
            await _queue.ListTasksAsync(200, cancellationToken).ConfigureAwait(false), _json)).ConfigureAwait(false);

    /// <summary>运行期上限与白名单。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerResource(
        UriTemplate = "devhub://limits",
        Name = "limits",
        Title = "运行期上限",
        MimeType = "application/json")]
    [Description("并发、预算、工具白名单与 provider 列表。")]
    public async Task<string> LimitsAsync(CancellationToken cancellationToken = default) =>
        await SafeAsync(async () => JsonSerializer.Serialize(
            await _limits.GetAsync(cancellationToken).ConfigureAwait(false), _json)).ConfigureAwait(false);

    /// <summary>统一兜底：资源读取失败时返回可解析的错误 JSON。</summary>
    /// <param name="factory">读取逻辑。</param>
    private static async Task<string> SafeAsync(Func<Task<string>> factory)
    {
        try
        {
            return await factory().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { error = exception.GetType().Name, message = exception.Message }, _json);
        }
    }
}
