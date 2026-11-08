namespace DevHubd.Core.Roles;

/// <summary>
/// 角色声明（<c>plugin/roles/*.json</c>）的运行时表示。角色是<b>数据</b>而不是新的代码层：
/// 宿主只负责加载、校验、快照，执行分支仍由 <c>TurnRunner</c> 统一承担，差异全部来自本记录。
/// </summary>
/// <param name="Id">角色标识，与文件名一致。</param>
/// <param name="Name">展示名。</param>
/// <param name="Description">能力描述：这个角色负责什么、不负责什么。</param>
/// <param name="Skills">绑定的技能目录名。</param>
/// <param name="Tools">可用 MCP 工具名。</param>
/// <param name="Delivery">交付上限：<c>auto</c> 或 <c>dispatch_only</c>。</param>
/// <param name="RequiresApproval">产出是否必须人工审批后才算交付。</param>
/// <param name="CanRequestInput">是否允许在回合内发起不确定项提问。</param>
/// <param name="MaxParallel">该角色同时运行的回合上限。</param>
/// <param name="MaxAttempts">该角色自动重试上限。</param>
/// <param name="MaxRounds">单回合模型轮次上限。</param>
/// <param name="MaxToolCalls">单回合工具调用上限。</param>
/// <param name="MaxDurationSeconds">单回合墙钟上限（秒）。</param>
/// <param name="MaxInputTokens">单回合输入 token 软上限；null 表示不限制。</param>
/// <param name="SourcePath">声明文件绝对路径。</param>
/// <param name="ContentHash">声明文件字节的 SHA-256（小写十六进制），用于判断快照是否需要更新。</param>
/// <param name="Payload">原始 JSON 文本，落 <c>role_view.payload</c>。</param>
public sealed record RoleDefinition(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Tools,
    string Delivery,
    bool RequiresApproval,
    bool CanRequestInput,
    int MaxParallel,
    int MaxAttempts,
    int MaxRounds,
    int MaxToolCalls,
    int MaxDurationSeconds,
    int? MaxInputTokens,
    string SourcePath,
    string ContentHash,
    string Payload);
