namespace DevHubd.Core.Models;

/// <summary>模型回合的终态。</summary>
public enum TurnState
{
    /// <summary>正常完成。</summary>
    Done,

    /// <summary>失败（原因见 <see cref="TurnResult.Error"/>）。</summary>
    Failed,

    /// <summary>被取消或超时。</summary>
    Cancelled,
}

/// <summary>
/// 错误分类常量，取值与设计文档 §13.1 的错误矩阵一一对应。
/// 用常量而非枚举，便于落库与跨进程传输时保持字符串稳定。
/// </summary>
public static class ModelErrorKinds
{
    /// <summary>408/5xx、连接拒绝、超时：可重试。</summary>
    public const string Transient = "transient";

    /// <summary>429 / insufficient_quota：该 provider 熔断。</summary>
    public const string Quota = "quota";

    /// <summary>401/403：该 provider 挂起，等人工。</summary>
    public const string Auth = "auth";

    /// <summary>模型不存在：该 provider 挂起，等人工。</summary>
    public const string NoModel = "no_model";

    /// <summary>发现文件缺失或桥不可达：延迟重试，不消耗预算。</summary>
    public const string BridgeDown = "bridge_down";

    /// <summary>模型内部错误或回合异常结束：有限重试。</summary>
    public const string ModelError = "model_error";

    /// <summary>工具自身失败：回合内由模型纠正。</summary>
    public const string ToolError = "tool_error";

    /// <summary>轮次/时长/写量超限：不重试，转人工复核。</summary>
    public const string BudgetExceeded = "budget_exceeded";

    /// <summary>租约丢失：立即终止，记 abandoned。</summary>
    public const string LeaseLost = "lease_lost";

    /// <summary>人工取消或停机：不重试。</summary>
    public const string UserCancelled = "user_cancelled";

    /// <summary>回合因缺少输入而暂停：记 abandoned，不消耗重试预算，等人回答后恢复。</summary>
    public const string NeedsInput = "needs_input";

    /// <summary>
    /// 资源类失败：provider / 桥自身不可用，与任务本身无关，不计入重试预算。
    /// 认领预算谓词与 <c>ExecutionStore.CountConsumingAttemptsAsync</c> 共用这一集合。
    /// </summary>
    public static readonly string[] BudgetExempt =
    {
        Auth,
        Quota,
        NoModel,
        BridgeDown,
    };
}

/// <summary>一次模型回合的错误描述。</summary>
/// <param name="Kind">错误分类，见 <see cref="ModelErrorKinds"/>。</param>
/// <param name="Code">稳定的错误码（大写下划线），用于落库与诊断。</param>
/// <param name="Message">可诊断的中文说明；不得包含凭据。</param>
public sealed record ModelError(string Kind, string Code, string Message);

/// <summary>token 用量。</summary>
/// <param name="InputTokens">输入 token 数。</param>
/// <param name="OutputTokens">输出 token 数。</param>
/// <param name="Estimated">是否为估算值（vscode 桥路径只能估算）。</param>
public sealed record TokenUsage(int InputTokens, int OutputTokens, bool Estimated);

/// <summary>暴露给模型的工具描述（白名单由 C# 决定）。</summary>
/// <param name="Name">工具名。</param>
/// <param name="Description">给模型看的说明。</param>
/// <param name="InputSchemaJson">JSON Schema 文本。</param>
public sealed record ToolDescriptor(string Name, string Description, string InputSchemaJson);

/// <summary>一个回合的预算上限，超限即终止并转人工复核。</summary>
/// <param name="MaxToolRounds">工具轮数上限。</param>
/// <param name="MaxDuration">单回合最长时长。</param>
/// <param name="MaxFilesChanged">最多改动文件数。</param>
/// <param name="MaxWrites">最多写入次数。</param>
/// <param name="MaxPatchBytes">单次写入最大字节数。</param>
/// <param name="MaxReadBytes">单次读取最大字节数。</param>
public sealed record BudgetLimits(
    int MaxToolRounds,
    TimeSpan MaxDuration,
    int MaxFilesChanged,
    int MaxWrites,
    long MaxPatchBytes,
    int MaxReadBytes);

/// <summary>
/// 一次模型回合的请求。携带 queueItemId / workspaceId / taskId 三个关联字段，
/// 使日志能把 <c>queueItemId → attempt → turnId</c> 串起来（P2 验收 1）。
/// </summary>
/// <param name="TurnId">回合 id，全链路关联键。</param>
/// <param name="SessionId">会话 id；P2 每个回合独立，等价于 turnId。</param>
/// <param name="Provider">provider：openai / vscode。</param>
/// <param name="QueueItemId">所属队列项。</param>
/// <param name="WorkspaceId">工作区 id。</param>
/// <param name="TaskId">任务 id。</param>
/// <param name="SystemPrompt">系统提示词。</param>
/// <param name="UserPrompt">任务指令。</param>
/// <param name="Tools">工具白名单。</param>
/// <param name="Budget">预算上限。</param>
/// <param name="Model">模型名；null 表示用 provider 默认值。</param>
/// <param name="Timeout">回合超时。</param>
public sealed record TurnRequest(
    string TurnId,
    string SessionId,
    string Provider,
    string QueueItemId,
    string WorkspaceId,
    string TaskId,
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<ToolDescriptor> Tools,
    BudgetLimits Budget,
    string? Model,
    TimeSpan Timeout);

/// <summary>一次模型回合的结果。失败以返回值表达，不抛异常。</summary>
/// <param name="State">终态。</param>
/// <param name="Text">模型文本输出。</param>
/// <param name="ToolCalls">工具调用次数。</param>
/// <param name="Usage">token 用量；null 表示 provider 未返回。</param>
/// <param name="FilesChanged">本回合写入的文件（相对写根，斜杠分隔）。</param>
/// <param name="Error">失败原因；成功时为 null。</param>
/// <param name="RoundsExhausted">
/// true 表示工具轮次预算已耗尽，文本是框架移除工具后的兜底答复。
/// 状态仍是 <see cref="TurnState.Done"/>（框架行为），但调用方必须据此区别于“自然完成”。
/// </param>
public sealed record TurnResult(
    TurnState State,
    string Text,
    int ToolCalls,
    TokenUsage? Usage,
    IReadOnlyList<string> FilesChanged,
    ModelError? Error,
    bool RoundsExhausted = false)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="text">模型文本。</param>
    /// <param name="toolCalls">工具调用次数。</param>
    /// <param name="usage">用量。</param>
    /// <param name="filesChanged">写入文件。</param>
    /// <param name="roundsExhausted">是否耗尽了工具轮次预算。</param>
    public static TurnResult Done(
        string text,
        int toolCalls,
        TokenUsage? usage,
        IReadOnlyList<string> filesChanged,
        bool roundsExhausted = false) =>
        new(TurnState.Done, text, toolCalls, usage, filesChanged, null, roundsExhausted);

    /// <summary>构造失败结果。</summary>
    /// <param name="error">错误。</param>
    /// <param name="toolCalls">已发生的工具调用次数。</param>
    /// <param name="usage">失败前已产生的用量；null 表示未获得或不可信。</param>
    public static TurnResult Failed(ModelError error, int toolCalls = 0, TokenUsage? usage = null) =>
        new(TurnState.Failed, string.Empty, toolCalls, usage, Array.Empty<string>(), error);

    /// <summary>构造取消结果。</summary>
    /// <param name="error">取消原因。</param>
    /// <param name="toolCalls">已发生的工具调用次数。</param>
    public static TurnResult Cancelled(ModelError error, int toolCalls = 0) =>
        new(TurnState.Cancelled, string.Empty, toolCalls, null, Array.Empty<string>(), error);
}

/// <summary>provider 可用性探测结果。</summary>
/// <param name="Available">是否可用。</param>
/// <param name="State">ready / down / no_model / quota / auth。</param>
/// <param name="Detail">不可用原因；不得包含凭据。</param>
/// <param name="Model">实际使用的模型名。</param>
public sealed record ModelHealth(bool Available, string State, string? Detail, string? Model);

/// <summary>工具执行结果。失败同样以返回值表达。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="Output">成功时的文本输出。</param>
/// <param name="ErrorCode">失败时的错误码。</param>
/// <param name="ErrorMessage">失败时的可诊断说明。</param>
public sealed record ToolExecutionResult(bool Success, string? Output, string? ErrorCode, string? ErrorMessage)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="output">文本输出。</param>
    public static ToolExecutionResult Ok(string output) => new(true, output, null, null);

    /// <summary>构造失败结果。</summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">说明。</param>
    public static ToolExecutionResult Fail(string code, string message) => new(false, null, code, message);
}
