namespace DevHubd.Core.Models;

/// <summary>
/// 错误码词表：模型通道（provider / 传输 / 工具）与 MCP 错误信封共用同一组稳定字符串。
/// 只新增、不改写既有取值，避免已落库的错误码与外部调用方契约漂移。
/// </summary>
public static class DevHubErrorCodes
{
    // ---------- 模型通道：配置与鉴权 ----------

    /// <summary>未配置 API key。</summary>
    public const string MissingApiKey = "MISSING_API_KEY";

    /// <summary>provider 鉴权被拒绝。</summary>
    public const string AuthFailed = "AUTH_FAILED";

    /// <summary>provider 未注册。</summary>
    public const string ProviderNotRegistered = "PROVIDER_NOT_REGISTERED";

    /// <summary>扩展桥不可用。</summary>
    public const string BridgeDown = "BRIDGE_DOWN";

    /// <summary>扩展桥返回了无法解析的响应。</summary>
    public const string BridgeBadResponse = "BRIDGE_BAD_RESPONSE";

    // ---------- 模型通道：资源与瞬时错误 ----------

    /// <summary>请求被限流。</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>该 provider 配额已耗尽。</summary>
    public const string QuotaExhausted = "QUOTA_EXHAUSTED";

    /// <summary>模型不存在或无权访问。</summary>
    public const string NoModel = "NO_MODEL";

    /// <summary>上游返回 5xx 或 408。</summary>
    public const string UpstreamError = "UPSTREAM_ERROR";

    /// <summary>端点不可达或连接失败。</summary>
    public const string NetworkError = "NETWORK_ERROR";

    /// <summary>请求超时。</summary>
    public const string RequestTimeout = "REQUEST_TIMEOUT";

    /// <summary>请求被 provider 拒绝（业务参数问题）。</summary>
    public const string RequestRejected = "REQUEST_REJECTED";

    /// <summary>响应无法解析或缺少必要字段。</summary>
    public const string MalformedResponse = "MALFORMED_RESPONSE";

    /// <summary>回合异常结束。</summary>
    public const string TurnException = "TURN_EXCEPTION";

    // ---------- 工具与公共语义 ----------

    /// <summary>工具参数非法。</summary>
    public const string InvalidArgument = "INVALID_ARGUMENT";

    /// <summary>未知工具。</summary>
    public const string UnknownTool = "UNKNOWN_TOOL";

    /// <summary>角色未声明该工具：越权调用被拒绝。</summary>
    public const string ToolNotAllowed = "TOOL_NOT_ALLOWED";

    /// <summary>预算（轮次 / 写入 / 时长）超限。</summary>
    public const string BudgetExceeded = "BUDGET_EXCEEDED";

    /// <summary>工具执行被取消（非停机取消）。</summary>
    public const string ToolCancelled = "TOOL_CANCELLED";

    /// <summary>未归类的内部错误。</summary>
    public const string Internal = "INTERNAL";

    // ---------- 不确定项回流 ----------

    /// <summary>任务因缺少输入而挂起，等待会话侧回答。</summary>
    public const string NeedsInput = "NEEDS_INPUT";

    /// <summary>当前角色不允许向用户提问。</summary>
    public const string InputNotAllowed = "INPUT_NOT_ALLOWED";

    /// <summary>任务内容在等待回答期间已变更，旧问题单被取消。</summary>
    public const string TaskChanged = "TASK_CHANGED";

    /// <summary>等待回答超时：问题单置 expired，任务失败。</summary>
    public const string InputTimeout = "INPUT_TIMEOUT";

    /// <summary>问题单已是终态（已回答 / 已过期 / 已撤销），不接受回答或撤销。</summary>
    public const string InputRequestClosed = "INPUT_REQUEST_CLOSED";

    /// <summary>问题单不存在。</summary>
    public const string UnknownInputRequest = "UNKNOWN_INPUT_REQUEST";

    /// <summary>答案不合法（长度或选项越界）。</summary>
    public const string InvalidAnswer = "INVALID_ANSWER";

    /// <summary>已回答的问题单收到不同答案，拒绝覆盖。</summary>
    public const string AnswerConflict = "ANSWER_CONFLICT";

    /// <summary>结果不存在。</summary>
    public const string UnknownResult = "UNKNOWN_RESULT";

    /// <summary>结果不处于待审批状态，不能通过或驳回。</summary>
    public const string ResultNotPending = "RESULT_NOT_PENDING";

    /// <summary>任务文件回写失败：审批/驳回未生效，可修复文件权限后重试。</summary>
    public const string TaskFileWriteFailed = "TASK_FILE_WRITE_FAILED";

    /// <summary>进程重启导致上一个进程的回合中断（attempt 兜底为 abandoned）。</summary>
    public const string ProcessRestart = "PROCESS_RESTART";

    /// <summary>队列项认领失败（attempt 写入冲突等），该项已推迟、不计入重试预算。</summary>
    public const string ClaimFailed = "CLAIM_FAILED";

    /// <summary>审批已落地，同任务其余非终态队列项被取消（通常是 force 造出的并行项）。</summary>
    public const string SupersededByDecision = "SUPERSEDED_BY_DECISION";
}
