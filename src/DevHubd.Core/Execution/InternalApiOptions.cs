namespace DevHubd.Core.Execution;

/// <summary>
/// 内部 API 的访问参数：宿主把 <c>/internal/*</c> 挂在与 MCP 相同的回环端口上，
/// 令牌在进程启动时随机生成、只写进 <c>runtime.json</c> 与内存，不进插件包、不进日志。
/// </summary>
/// <param name="BaseUrl">内部 API 基地址（仅回环）。</param>
/// <param name="Token">Bearer 令牌。</param>
public sealed record InternalApiOptions(string BaseUrl, string Token);
