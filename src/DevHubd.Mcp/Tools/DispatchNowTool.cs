using System.ComponentModel;
using System.Text.Json;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary><c>dispatch_now</c>：立即把指定任务翻译成队列项（不调用模型）。</summary>
[McpServerToolType]
public sealed class DispatchNowTool
{
    private readonly IDispatchService _dispatcher;
    private readonly IToolDigestSource _digest;

    /// <summary>注入派发服务与摘要源。</summary>
    /// <param name="dispatcher">派发服务。</param>
    /// <param name="digest">摘要数据源。</param>
    public DispatchNowTool(IDispatchService dispatcher, IToolDigestSource digest)
    {
        _dispatcher = dispatcher;
        _digest = digest;
    }

    /// <summary>把单个任务入队；<c>dryRun=true</c> 只判定不写入。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="workspaceId">可选工作区 id。</param>
    /// <param name="provider">auto / openai / vscode。</param>
    /// <param name="dryRun">是否只判定不写入。</param>
    /// <param name="force">跳过 manual 暂停，并显式覆盖"已有活跃队列项或待审结果"的入队闸门（写审计与 queue_item 标记）。</param>
    /// <param name="requestId">可选幂等键（1–100 字符）；同键重复调用返回首次创建的队列项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "dispatch_now", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("立即把指定任务加入调度队列。默认 dryRun=false 会写入队列；dryRun=true 只返回判定结果。显式指定 provider 时会先校验其可用性。提供 requestId 时同一键重复调用幂等返回首次的队列项。同一工作区 + 任务已有待审结果时默认拒绝，确需覆盖须显式 force=true。")]
    public async Task<CallToolResult> DispatchAsync(
        [Description("任务 id（.devhub/tasks 中的 id）。")] string taskId,
        [Description("可选：工作区 id；同名任务存在于多个工作区时必须指定。")] string? workspaceId = null,
        [Description("首选 provider：" + ToolVocabulary.Providers + "（auto 为默认，按可用性选择）。")] string provider = "auto",
        [Description("true 时只判定不写入。")] bool dryRun = false,
        [Description("true 时跳过 manual 暂停，并覆盖「已有活跃队列项 / 待审结果」的入队闸门（会写审计）；quota/auth 等硬暂停不可覆盖。")] bool force = false,
        [Description("可选幂等键（1–100 字符）：同一键重复调用返回首次创建的队列项。")] string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim();
        if (normalizedRequestId is { Length: > 100 })
        {
            return await FailureAsync(
                "INVALID_ARGUMENT",
                "requestId 长度为 1–100 字符。",
                retryable: true,
                next: "缩短 requestId 后重试。",
                details: new { length = normalizedRequestId.Length },
                cancellationToken).ConfigureAwait(false);
        }

        var outcome = await _dispatcher
            .DispatchNowAsync(taskId, workspaceId, provider, dryRun, force, normalizedRequestId, cancellationToken)
            .ConfigureAwait(false);

        switch (outcome.Status)
        {
            case DispatchStatus.Enqueued:
            case DispatchStatus.Deduped:
            case DispatchStatus.ActiveExists:
            case DispatchStatus.WouldEnqueue:
            {
                var result = new DispatchNowResult(
                    taskId,
                    outcome.WorkspaceId,
                    outcome.Status.ToString().ToLowerInvariant(),
                    outcome.QueueItemId,
                    outcome.Provider,
                    dryRun);

                return await ToolResults
                    .SuccessAsync(JsonSerializer.Serialize(result, ToolJson.Options), _digest, cancellationToken)
                    .ConfigureAwait(false);
            }

            case DispatchStatus.UnknownTask:
                return await FailureAsync(
                    "UNKNOWN_TASK",
                    "任务不存在于任何已登记工作区。",
                    retryable: false,
                    next: "确认 .devhub/tasks 下存在该任务，并检查 workspaces.json 是否包含该工作区。",
                    details: new { taskId, workspaceId },
                    cancellationToken);

            case DispatchStatus.AmbiguousTask:
                return await FailureAsync(
                    "AMBIGUOUS_TASK",
                    "同一任务 id 存在于多个工作区。",
                    retryable: true,
                    next: "在参数中显式指定 workspaceId。",
                    details: new { taskId, candidates = outcome.Candidates },
                    cancellationToken);

            case DispatchStatus.TaskNotReady:
                return await FailureAsync(
                    "TASK_NOT_READY",
                    "任务状态不是 ready，不能派发。",
                    retryable: false,
                    next: "把任务文件的 status 改为 ready 后重试。",
                    details: new { taskId, workspaceId = outcome.WorkspaceId },
                    cancellationToken);

            case DispatchStatus.PendingReview:
                return await FailureAsync(
                    "RESULT_AWAITING_REVIEW",
                    "任务已有待审结果，不能并行派发。",
                    retryable: true,
                    next: "先用 result_approve / result_reject 决策，或显式传 force=true 覆盖。",
                    details: new { taskId, workspaceId = outcome.WorkspaceId },
                    cancellationToken);

            case DispatchStatus.SchedulerPaused:
                return await FailureAsync(
                    "SCHEDULER_PAUSED",
                    "调度器已暂停，拒绝新的派发。",
                    retryable: true,
                    next: "先用 scheduler_control 执行 resume，或用 force=true 覆盖 manual 暂停。",
                    details: null,
                    cancellationToken);

            case DispatchStatus.ProviderUnavailable:
                return await FailureAsync(
                    "PROVIDER_UNAVAILABLE",
                    $"provider {outcome.Provider} 当前不可用。",
                    retryable: true,
                    next: "查看 scheduler_status.providers 的 state/detail；确认凭据或扩展桥后重试。",
                    details: new { provider = outcome.Provider },
                    cancellationToken);

            case DispatchStatus.NoModelProvider:
                return await FailureAsync(
                    "NO_MODEL_PROVIDER",
                    "没有任何可用 provider，无法按 auto 派发。",
                    retryable: true,
                    next: "配置 DEVHUB_OPENAI_API_KEY，或启动 VS Code 扩展桥后重试。",
                    details: null,
                    cancellationToken);

            default:
                return await FailureAsync(
                    "INVALID_PROVIDER",
                    "provider 取值非法。",
                    retryable: true,
                    next: "使用 auto / openai / vscode 之一。",
                    details: new { provider },
                    cancellationToken);
        }
    }

    /// <summary>构造带摘要的错误结果。</summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">消息。</param>
    /// <param name="retryable">是否可重试。</param>
    /// <param name="next">下一步提示。</param>
    /// <param name="details">附加上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private Task<CallToolResult> FailureAsync(
        string code,
        string message,
        bool retryable,
        string? next,
        object? details,
        CancellationToken cancellationToken) =>
        ToolResults.FailureAsync(
            McpErrors.Envelope(code, message, retryable, next, details),
            _digest,
            cancellationToken);
}
