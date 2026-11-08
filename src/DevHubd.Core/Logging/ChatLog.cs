using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Logging;

/// <summary>
/// 模型通道的结构化日志事件。只记录摘要字段（provider、模型、消息数、工具数、用量、耗时），
/// 绝不记录提示词、文件内容或工具参数，避免工作区数据进入日志。
/// </summary>
internal static partial class ChatLog
{
    /// <summary>一次模型请求开始（工具循环已开始，后续迭代不重复记录）。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="provider">provider 标识。</param>
    /// <param name="model">模型名。</param>
    /// <param name="messageCount">消息条数。</param>
    /// <param name="toolCount">可用工具数。</param>
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "模型请求开始：provider={Provider} model={Model} messages={MessageCount} tools={ToolCount}")]
    public static partial void RequestStarted(ILogger logger, string provider, string? model, int messageCount, int toolCount);

    /// <summary>模型请求完成。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="provider">provider 标识。</param>
    /// <param name="model">模型名。</param>
    /// <param name="finishReason">结束原因。</param>
    /// <param name="inputTokens">输入 token（不可得为 null）。</param>
    /// <param name="outputTokens">输出 token（不可得为 null）。</param>
    /// <param name="elapsedMs">耗时（毫秒）。</param>
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "模型请求完成：provider={Provider} model={Model} finishReason={FinishReason} inputTokens={InputTokens} outputTokens={OutputTokens} elapsedMs={ElapsedMs}")]
    public static partial void RequestCompleted(
        ILogger logger,
        string provider,
        string? model,
        string? finishReason,
        long? inputTokens,
        long? outputTokens,
        long elapsedMs);

    /// <summary>模型请求失败（异常只记录类型，避免把响应体或参数写进日志）。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="exception">原始异常。</param>
    /// <param name="provider">provider 标识。</param>
    /// <param name="model">模型名。</param>
    /// <param name="exceptionType">异常类型名。</param>
    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "模型请求失败：provider={Provider} model={Model} exceptionType={ExceptionType}")]
    public static partial void RequestFailed(
        ILogger logger,
        Exception exception,
        string provider,
        string? model,
        string exceptionType);

    /// <summary>模型请求被取消（停机或回合超时，属于预期路径，不记异常栈）。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="provider">provider 标识。</param>
    /// <param name="model">模型名。</param>
    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "模型请求已取消：provider={Provider} model={Model}")]
    public static partial void RequestCancelled(ILogger logger, string provider, string? model);

    /// <summary>工具调用失败（业务码与迭代序号；不记录参数）。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="exception">内部异常；业务失败为 null。</param>
    /// <param name="toolName">工具名。</param>
    /// <param name="errorCode">稳定错误码。</param>
    /// <param name="iteration">工具循环迭代序号。</param>
    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "工具调用失败：tool={ToolName} errorCode={ErrorCode} iteration={Iteration}")]
    public static partial void ToolFailed(
        ILogger logger,
        Exception? exception,
        string toolName,
        string errorCode,
        int iteration);

    /// <summary>扩展桥回合失败。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="exception">通信或解析异常；受控失败为 null。</param>
    /// <param name="turnId">回合 id。</param>
    /// <param name="errorCode">稳定错误码。</param>
    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Error,
        Message = "扩展桥回合失败：turnId={TurnId} errorCode={ErrorCode}")]
    public static partial void BridgeTurnFailed(ILogger logger, Exception? exception, string turnId, string errorCode);

    /// <summary>取消扩展桥回合失败（仅诊断）。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="exception">取消请求异常。</param>
    /// <param name="turnId">回合 id。</param>
    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Debug,
        Message = "取消扩展桥回合失败：turnId={TurnId}")]
    public static partial void BridgeCancelFailed(ILogger logger, Exception exception, string turnId);
}
