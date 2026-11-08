namespace DevHubd.Mcp.Abstractions;

/// <summary>任务写入结果状态。</summary>
public enum TaskUpsertStatus
{
    /// <summary>已原子写入任务文件。</summary>
    Written,

    /// <summary>工作区不存在或未启用。</summary>
    UnknownWorkspace,

    /// <summary>未指定 workspaceId 且存在多个已启用工作区。</summary>
    AmbiguousWorkspace,

    /// <summary>任务字段不合法。</summary>
    InvalidTask,

    /// <summary>角色未在 <c>role_view</c> 中声明。</summary>
    UnknownRole,

    /// <summary>任务正在执行（存在未结束的队列项），拒绝改写。</summary>
    Conflict,
}

/// <summary>任务写入请求。</summary>
/// <param name="WorkspaceId">可选工作区 id。</param>
/// <param name="Id">任务 id。</param>
/// <param name="Title">标题。</param>
/// <param name="Prompt">给模型的指令。</param>
/// <param name="Priority">优先级 0–1000。</param>
/// <param name="Role">执行角色；null 表示 implementer。</param>
/// <param name="Delivery">交付档位；null 表示 review。</param>
/// <param name="WriteScope">写白名单。</param>
/// <param name="DependsOn">前置任务 id。</param>
/// <param name="RequiresApproval">是否必须人工审批。</param>
/// <param name="Source">来源渠道（可选）。</param>
public sealed record TaskUpsertRequest(
    string? WorkspaceId,
    string Id,
    string Title,
    string Prompt,
    int Priority,
    string? Role,
    string? Delivery,
    IReadOnlyList<string>? WriteScope,
    IReadOnlyList<string>? DependsOn,
    bool RequiresApproval,
    string? Source);

/// <summary>任务写入结果。</summary>
/// <param name="Status">结果状态。</param>
/// <param name="WorkspaceId">解析后的工作区；失败时可能为空。</param>
/// <param name="TaskId">任务 id。</param>
/// <param name="Path">写入路径（仅成功）。</param>
/// <param name="ContentHash">写入内容的 SHA-256（仅成功）。</param>
/// <param name="Queued">相同内容是否已存在队列项（将被去重）。</param>
/// <param name="Reason">失败原因。</param>
/// <param name="Candidates">歧义时的工作区候选。</param>
public sealed record TaskUpsertOutcome(
    TaskUpsertStatus Status,
    string WorkspaceId,
    string TaskId,
    string? Path,
    string? ContentHash,
    bool Queued,
    string? Reason,
    IReadOnlyList<string>? Candidates);

/// <summary>任务写入服务：把任务声明原子写入 <c>&lt;workspace&gt;/.devhub/tasks/</c>，由扫描器拾取。</summary>
public interface ITaskWriteService
{
    /// <summary>写入或覆盖任务文件。</summary>
    /// <param name="request">写入请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TaskUpsertOutcome> UpsertAsync(TaskUpsertRequest request, CancellationToken cancellationToken = default);
}
