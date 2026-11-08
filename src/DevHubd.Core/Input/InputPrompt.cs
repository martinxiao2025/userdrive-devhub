namespace DevHubd.Core.Input;

/// <summary>问题单的一个可选项（给模型侧与人工侧共用）。</summary>
/// <param name="Id">选项 id（在单条问题内唯一）。</param>
/// <param name="Label">面向人的标签。</param>
/// <param name="Value">选项的机器值；null 表示与 label 相同语义。</param>
/// <param name="Recommended">是否为推荐项。</param>
public sealed record InputOption(string Id, string Label, string? Value, bool Recommended);

/// <summary>
/// 模型侧一次提问的入参（已由 <c>ToolDispatcher</c> 做基本校验与长度限制）。
/// 只允许摘要、相对路径与已脱敏片段，禁止携带凭据。
/// </summary>
/// <param name="Question">面向人的一句话问题。</param>
/// <param name="Context">上下文摘要；可为 null。</param>
/// <param name="Options">可选项；空数组表示必须自由文本回答。</param>
/// <param name="MissingInputs">缺少的输入项。</param>
public sealed record InputPrompt(
    string Question,
    string? Context,
    IReadOnlyList<InputOption> Options,
    IReadOnlyList<string> MissingInputs);

/// <summary>问题单落库结果。</summary>
/// <param name="Accepted">是否被接受（已有 open 请求时也视为接受，复用既有 id）。</param>
/// <param name="RequestId">问题单 id。</param>
/// <param name="Created">true 表示本次新建；false 表示复用了既有 open 请求。</param>
/// <param name="ErrorCode">拒绝时的错误码。</param>
/// <param name="ErrorMessage">拒绝时的说明。</param>
public sealed record InputAcceptance(
    bool Accepted,
    string? RequestId,
    bool Created,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>构造接受结果。</summary>
    /// <param name="requestId">问题单 id。</param>
    /// <param name="created">是否本次新建。</param>
    public static InputAcceptance Ok(string requestId, bool created) => new(true, requestId, created, null, null);

    /// <summary>构造拒绝结果。</summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">说明。</param>
    public static InputAcceptance Fail(string code, string message) => new(false, null, false, code, message);
}
