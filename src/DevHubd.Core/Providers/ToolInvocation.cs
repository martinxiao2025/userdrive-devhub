using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Logging;
using DevHubd.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Providers;

/// <summary>
/// 把工具白名单描述适配为 <see cref="AIFunction"/>，供 <see cref="ChatOptions.Tools"/> 与
/// <c>FunctionInvokingChatClient</c> 发现；实际执行统一交给 <see cref="ToolInvoker"/>。
/// </summary>
internal sealed class DispatcherAIFunction : AIFunction
{
    private readonly ToolDescriptor _descriptor;
    private readonly IToolDispatcher _dispatcher;
    private readonly string _turnId;
    private readonly ILogger _logger;
    private readonly JsonElement _schema;

    /// <summary>绑定一个工具描述与本次回合的执行器。</summary>
    /// <param name="descriptor">工具描述。</param>
    /// <param name="dispatcher">本次回合的工具执行器。</param>
    /// <param name="turnId">回合 id。</param>
    /// <param name="logger">日志器。</param>
    public DispatcherAIFunction(ToolDescriptor descriptor, IToolDispatcher dispatcher, string turnId, ILogger logger)
    {
        _descriptor = descriptor;
        _dispatcher = dispatcher;
        _turnId = turnId;
        _logger = logger;

        using var document = JsonDocument.Parse(descriptor.InputSchemaJson);
        _schema = document.RootElement.Clone();
    }

    /// <inheritdoc />
    public override string Name => _descriptor.Name;

    /// <inheritdoc />
    public override string Description => _descriptor.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _schema;

    /// <summary>
    /// 兜底执行路径：正常调用由 <c>FunctionInvokingChatClient.FunctionInvoker</c> 接管，
    /// 这里保持同一实现，避免组合方漏设 invoker 时工具静默失效。
    /// </summary>
    /// <param name="arguments">工具参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken) =>
        ToolInvoker.InvokeAsync(
            _dispatcher,
            _turnId,
            Name,
            arguments,
            iteration: 0,
            logger: _logger,
            cancellationToken: cancellationToken);
}

/// <summary>
/// 工具执行与错误语义化：业务失败转换为携带稳定错误码的受控异常，
/// 由 <c>FunctionInvokingChatClient</c> 决定是否回灌给模型、是否继续循环；本类不做任何循环控制。
/// </summary>
internal static class ToolInvoker
{
    /// <summary>执行一次工具调用，返回回灌给模型的内容。</summary>
    /// <param name="dispatcher">本次回合的工具执行器。</param>
    /// <param name="turnId">回合 id。</param>
    /// <param name="toolName">工具名（来自模型调用）。</param>
    /// <param name="arguments">工具参数。</param>
    /// <param name="iteration">工具循环迭代序号（由框架提供）。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async ValueTask<object?> InvokeAsync(
        IToolDispatcher dispatcher,
        string turnId,
        string toolName,
        AIFunctionArguments arguments,
        int iteration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        JsonElement payload;
        try
        {
            payload = JsonSerializer.SerializeToElement(
                arguments.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            ChatLog.ToolFailed(logger, exception, toolName, DevHubErrorCodes.InvalidArgument, iteration);
            throw new ToolInvocationException(
                ModelErrorKinds.ModelError,
                DevHubErrorCodes.InvalidArgument,
                "工具参数无法序列化为 JSON。");
        }

        try
        {
            var result = await dispatcher
                .ExecuteAsync(turnId, toolName, payload, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                return result.Output ?? string.Empty;
            }

            var code = result.ErrorCode ?? DevHubErrorCodes.Internal;
            ChatLog.ToolFailed(logger, null, toolName, code, iteration);
            throw new ToolInvocationException(
                ModelErrorKinds.ToolError,
                code,
                result.ErrorMessage ?? "工具执行失败。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停机 / 回合超时：原样抛出，由外层按取消语义处理。
            throw;
        }
        catch (OperationCanceledException)
        {
            ChatLog.ToolFailed(logger, null, toolName, DevHubErrorCodes.ToolCancelled, iteration);
            throw new ToolInvocationException(
                ModelErrorKinds.Transient,
                DevHubErrorCodes.ToolCancelled,
                "工具执行被取消。");
        }
        catch (ToolInvocationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 未知异常只进日志，回灌给模型的是脱敏后的稳定错误码。
            ChatLog.ToolFailed(logger, exception, toolName, DevHubErrorCodes.Internal, iteration);
            throw new ToolInvocationException(
                ModelErrorKinds.ModelError,
                DevHubErrorCodes.Internal,
                "工具执行发生内部错误。");
        }
    }
}

/// <summary>工具调用受控异常：携带稳定错误码，消息可直接回灌给模型。</summary>
internal sealed class ToolInvocationException : Exception
{
    /// <summary>构造受控失败。</summary>
    /// <param name="kind">错误分类，见 <see cref="ModelErrorKinds"/>。</param>
    /// <param name="code">稳定错误码。</param>
    /// <param name="message">脱敏后的中文说明。</param>
    public ToolInvocationException(string kind, string code, string message)
        : base($"[{code}] {message}")
    {
        Kind = kind;
        Code = code;
    }

    /// <summary>错误分类。</summary>
    public string Kind { get; }

    /// <summary>稳定错误码。</summary>
    public string Code { get; }
}
