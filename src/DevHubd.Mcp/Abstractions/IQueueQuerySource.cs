using DevHubd.Mcp.Contracts;

namespace DevHubd.Mcp.Abstractions;

/// <summary>队列只读查询。由宿主实现，工具层只做整形与错误封装。</summary>
public interface IQueueQuerySource
{
    /// <summary>分页列出队列项。</summary>
    /// <param name="status">状态过滤；<c>all</c> 表示不过滤。</param>
    /// <param name="limit">1–100。</param>
    /// <param name="cursor">上一页的 nextCursor。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<QueuePageResult> ListAsync(string status, int limit, string? cursor, CancellationToken cancellationToken = default);

    /// <summary>读取单个队列项详情；不存在返回 null。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<QueueItemDetailResult?> GetAsync(string queueItemId, CancellationToken cancellationToken = default);

    /// <summary>列出待派发与执行中的队列项（只读资源使用）。</summary>
    /// <param name="limit">最多返回条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<QueueItemSummary>> ListActiveAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>列出任务物化视图（只读资源使用）。</summary>
    /// <param name="limit">最多返回条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<TaskListItemResult>> ListTasksAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>任务物化视图条目（只读资源 <c>devhub://tasks</c>）。</summary>
public sealed record TaskListItemResult(
    string WorkspaceId,
    string TaskId,
    string Title,
    string Status,
    int Priority,
    string RoleId,
    string SeenAt);

/// <summary>手动派发结果状态。</summary>
public enum DispatchStatus
{
    /// <summary>已入队。</summary>
    Enqueued,

    /// <summary>同一 dedupe_key 已入队过。</summary>
    Deduped,

    /// <summary>该任务已有未结束的队列项。</summary>
    ActiveExists,

    /// <summary>dryRun 下判定为"可以入队"。</summary>
    WouldEnqueue,

    /// <summary>任务不存在。</summary>
    UnknownTask,

    /// <summary>任务 id 在多个工作区同时存在，需要显式指定 workspaceId。</summary>
    AmbiguousTask,

    /// <summary>任务状态不是 ready。</summary>
    TaskNotReady,

    /// <summary>调度器已暂停。</summary>
    SchedulerPaused,

    /// <summary>provider 取值非法。</summary>
    InvalidProvider,

    /// <summary>显式指定的 provider 当前不可用（未配置 / 熔断打开 / 扩展桥不在线）。</summary>
    ProviderUnavailable,

    /// <summary>auto 模式下没有任何可用 provider。</summary>
    NoModelProvider,
}

/// <summary>手动派发结果。</summary>
public sealed record DispatchOutcome(
    DispatchStatus Status,
    string WorkspaceId,
    string? QueueItemId,
    string? Provider,
    IReadOnlyList<string>? Candidates);

/// <summary>手动派发（不调用模型，只把任务翻译成队列项）。</summary>
public interface IDispatchService
{
    /// <summary>把单个任务入队。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="workspaceId">可选工作区 id；省略时自动解析。</param>
    /// <param name="provider">auto / openai / vscode。</param>
    /// <param name="dryRun">只判定不写入。</param>
    /// <param name="force">忽略 manual 暂停强行入队。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<DispatchOutcome> DispatchNowAsync(
        string taskId,
        string? workspaceId,
        string provider,
        bool dryRun,
        bool force,
        CancellationToken cancellationToken = default);
}

/// <summary>调度器控制面（暂停 / 恢复 / 立即扫描 / 档位 / 熔断 / 探测 / 读取状态）。</summary>
public interface ISchedulerControl
{
    /// <summary>读取当前控制面状态。</summary>
    Task<SchedulerControlSnapshot> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>启动调度（等价于 resume，保留独立动作名以对齐文档）。</summary>
    Task<SchedulerControlSnapshot> StartAsync(string? reason, CancellationToken cancellationToken = default);

    /// <summary>暂停扫描与入队。</summary>
    Task<SchedulerControlSnapshot> PauseAsync(string? reason, CancellationToken cancellationToken = default);

    /// <summary>恢复扫描与入队。</summary>
    Task<SchedulerControlSnapshot> ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>请求立即扫描一次。</summary>
    Task<SchedulerControlSnapshot> RequestScanAsync(CancellationToken cancellationToken = default);

    /// <summary>切换交付档位（auto / dispatch_only）。</summary>
    Task<SchedulerControlSnapshot> SetModeAsync(string mode, CancellationToken cancellationToken = default);

    /// <summary>人工重置某个 provider 的熔断。</summary>
    Task<SchedulerControlSnapshot> ResetBreakerAsync(
        string provider,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>立即探测所有 provider 的模型可用性并落库。</summary>
    Task<SchedulerControlSnapshot> ProbeModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 控制面拒绝动作时抛出的领域异常；工具层据此生成 <c>McpErrors.Envelope</c>，
/// 避免在契约里塞 None/Error 双形态。
/// </summary>
/// <param name="code">大写下划线错误码（如 <c>MODE_LOCKED</c>）。</param>
/// <param name="message">可诊断的中文消息。</param>
/// <param name="next">下一步动作提示。</param>
/// <param name="retryable">是否值得重试。</param>
public sealed class SchedulerControlException(string code, string message, string? next = null, bool retryable = false)
    : Exception(message)
{
    /// <summary>错误码。</summary>
    public string Code { get; } = code;

    /// <summary>下一步动作提示。</summary>
    public string? Next { get; } = next;

    /// <summary>是否值得重试。</summary>
    public bool Retryable { get; } = retryable;
}

