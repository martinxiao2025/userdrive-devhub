using System.ComponentModel;
using System.Text.Json;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary>
/// <c>task_upsert</c>：把任务声明原子写入工作区 <c>.devhub/tasks/</c>。
/// 只写文件、不写数据库；扫描器在下一轮拾取并物化到 <c>task_view</c>。
/// </summary>
[McpServerToolType]
public sealed class TaskUpsertTool
{
    private readonly ITaskWriteService _tasks;
    private readonly IToolDigestSource _digest;

    /// <summary>注入任务写入服务与摘要源。</summary>
    /// <param name="tasks">任务写入服务。</param>
    /// <param name="digest">摘要数据源。</param>
    public TaskUpsertTool(ITaskWriteService tasks, IToolDigestSource digest)
    {
        _tasks = tasks;
        _digest = digest;
    }

    /// <summary>写入或覆盖任务文件。</summary>
    /// <param name="id">任务 id。</param>
    /// <param name="title">标题。</param>
    /// <param name="prompt">给模型的指令。</param>
    /// <param name="workspaceId">可选工作区 id。</param>
    /// <param name="priority">优先级 0–1000。</param>
    /// <param name="role">执行角色；缺省 implementer。</param>
    /// <param name="delivery">交付档位；缺省 review。</param>
    /// <param name="writeScope">写白名单（相对工作区根）。</param>
    /// <param name="dependsOn">前置任务 id。</param>
    /// <param name="requiresApproval">是否必须人工审批。</param>
    /// <param name="source">来源渠道（可选，仅用于追溯）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "task_upsert", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("创建或覆盖一个任务文件（.devhub/tasks/<id>.json），由扫描器在下一轮拾取。相同内容会被去重，不会重复入队。")]
    public async Task<CallToolResult> UpsertAsync(
        [Description("任务 id，匹配 ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$。")] string id,
        [Description("任务标题，1–200 字符。")] string title,
        [Description("给模型的指令。")] string prompt,
        [Description("可选：工作区 id；存在多个已启用工作区时必须指定。")] string? workspaceId = null,
        [Description("优先级 0–1000，越大越先派发。")] int priority = 0,
        [Description("执行角色 id，必须是 role_view 中已声明的角色；缺省 implementer。")] string? role = null,
        [Description("交付档位：" + ToolVocabulary.TaskDeliveries + "；缺省 review。")] string? delivery = null,
        [Description("相对工作区根的写入白名单。")] string[]? writeScope = null,
        [Description("前置任务 id 列表；任一未 done 时任务视图为 blocked。")] string[]? dependsOn = null,
        [Description("true 时产出必须人工审批后才算交付。")] bool requiresApproval = false,
        [Description("来源渠道（可选，最长 200 字符），仅用于追溯。")] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var request = new TaskUpsertRequest(
            workspaceId,
            id,
            title,
            prompt,
            priority,
            role,
            delivery,
            writeScope,
            dependsOn,
            requiresApproval,
            source);

        var outcome = await _tasks.UpsertAsync(request, cancellationToken).ConfigureAwait(false);

        switch (outcome.Status)
        {
            case TaskUpsertStatus.Written:
            {
                var result = new TaskUpsertResult(
                    outcome.TaskId,
                    outcome.Path!,
                    outcome.ContentHash!,
                    outcome.Queued);

                return await ToolResults
                    .SuccessAsync(JsonSerializer.Serialize(result, ToolJson.Options), _digest, cancellationToken)
                    .ConfigureAwait(false);
            }

            case TaskUpsertStatus.UnknownWorkspace:
                return await FailureAsync(
                    "UNKNOWN_WORKSPACE",
                    "工作区不存在或未启用。",
                    retryable: false,
                    next: "先运行 devhubd --init-workspace，或检查 workspaces.json 中的 id。",
                    details: new { workspaceId, taskId = id },
                    cancellationToken);

            case TaskUpsertStatus.AmbiguousWorkspace:
                return await FailureAsync(
                    "AMBIGUOUS_WORKSPACE",
                    "存在多个已启用工作区。",
                    retryable: true,
                    next: "在参数中显式指定 workspaceId。",
                    details: new { candidates = outcome.Candidates },
                    cancellationToken);

            case TaskUpsertStatus.UnknownRole:
                return await FailureAsync(
                    "INVALID_ROLE",
                    outcome.Reason ?? "角色未在 role_view 中声明。",
                    retryable: false,
                    next: "先用 scheduler_status.roles 查看可用角色，或修正 plugin/roles 下的声明。",
                    details: new { taskId = id, role },
                    cancellationToken);

            case TaskUpsertStatus.Conflict:
                return await FailureAsync(
                    "TASK_CONFLICT",
                    "任务正在执行，拒绝改写其文件。",
                    retryable: true,
                    next: "等待队列项结束后重试；需要并行版本时使用新的任务 id。",
                    details: new { taskId = id, workspaceId = outcome.WorkspaceId },
                    cancellationToken);

            default:
                return await FailureAsync(
                    "INVALID_TASK",
                    outcome.Reason ?? "任务字段不合法。",
                    retryable: false,
                    next: "按 schemas/task.schema.json 修正参数后重试。",
                    details: new { taskId = id },
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
