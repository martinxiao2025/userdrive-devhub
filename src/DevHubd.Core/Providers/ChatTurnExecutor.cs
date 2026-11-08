using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Providers;

/// <summary>
/// 受控失败：桥或适配器已把异常归类为稳定错误码，执行器直接据此生成 <see cref="TurnResult"/>。
/// </summary>
public sealed class ChatTurnException : Exception
{
    /// <summary>构造受控失败。</summary>
    /// <param name="error">已分类的错误描述。</param>
    public ChatTurnException(ModelError error)
        : base(error.Message)
    {
        Error = error;
    }

    /// <summary>已分类的错误描述。</summary>
    public ModelError Error { get; }
}

/// <summary>
/// 一次回合的模型管道：<c>FunctionInvokingChatClient</c> 负责工具循环，
/// 内侧的 <see cref="UsageTrackingChatClient"/> 记录每轮 usage 是否完整。
/// </summary>
public sealed class ChatPipeline : IDisposable
{
    private readonly UsageTrackingChatClient _usageTracker;

    /// <summary>绑定组合好的客户端与用量跟踪器。</summary>
    /// <param name="client">已组合的客户端。</param>
    /// <param name="usageTracker">用量跟踪器。</param>
    internal ChatPipeline(IChatClient client, UsageTrackingChatClient usageTracker)
    {
        Client = client;
        _usageTracker = usageTracker;
    }

    /// <summary>组合后的客户端。</summary>
    public IChatClient Client { get; }

    /// <summary>是否每一轮都返回了可用 usage。</summary>
    public bool AllRoundsReportedUsage => _usageTracker.AllRoundsReportedUsage;

    /// <inheritdoc />
    public void Dispose() => Client.Dispose();
}

/// <summary>
/// 回合执行器的公共部分：把 <see cref="TurnRequest"/> 翻译为 <see cref="ChatOptions"/> 与消息，
/// 调用 <see cref="IChatClient"/>，再把响应/异常收敛为 <see cref="TurnResult"/>。
/// 轮次上限、连续错误计数、取消传播均交给 <c>FunctionInvokingChatClient</c> 与取消令牌，不在此重复实现。
/// </summary>
public static class ChatTurnExecutor
{
    /// <summary>响应附加属性键：桥报告的工具调用次数。</summary>
    public const string ToolCallsProperty = "devhub.toolCalls";

    /// <summary>响应附加属性键：桥报告的改动文件列表。</summary>
    public const string FilesChangedProperty = "devhub.filesChanged";

    /// <summary>
    /// 组合工具循环：外层为 <c>FunctionInvokingChatClient</c>，工具执行与错误语义化交给
    /// <see cref="ToolInvoker"/>；内部日志器置空，避免框架在 Debug/Trace 级别输出工具参数。
    /// </summary>
    /// <param name="innerClient">底层 provider 客户端（OpenAI 或桥适配器）。</param>
    /// <param name="request">回合请求。</param>
    /// <param name="dispatcher">本次回合的工具执行器。</param>
    /// <param name="logger">日志器（工具失败时使用）。</param>
    /// <param name="decorate">可选的最外层装饰（如脱敏日志中间件）。</param>
    public static ChatPipeline CreatePipeline(
        IChatClient innerClient,
        TurnRequest request,
        IToolDispatcher dispatcher,
        ILogger logger,
        Func<IChatClient, IChatClient>? decorate = null)
    {
        var usageTracker = new UsageTrackingChatClient(innerClient);
        IChatClient client = new FunctionInvokingChatClient(usageTracker, loggerFactory: null)
        {
            MaximumIterationsPerRequest = request.Budget.MaxToolRounds,
            IncludeDetailedErrors = true,
            FunctionInvoker = (context, cancellationToken) => ToolInvoker.InvokeAsync(
                dispatcher,
                request.TurnId,
                context.Function.Name,
                context.Arguments,
                context.Iteration,
                logger,
                cancellationToken),
        };

        if (decorate is not null)
        {
            client = decorate(client);
        }

        return new ChatPipeline(client, usageTracker);
    }

    /// <summary>
    /// 执行一个模型回合。业务失败以 <see cref="TurnResult"/> 返回；
    /// 取消令牌已取消时原样抛出 <see cref="OperationCanceledException"/>，由上层决定停机/超时语义。
    /// </summary>
    /// <param name="pipeline">已组合好的模型管道。</param>
    /// <param name="request">回合请求。</param>
    /// <param name="dispatcher">本次回合的工具执行器。</param>
    /// <param name="mapException">provider 级异常映射。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<TurnResult> RunAsync(
        ChatPipeline pipeline,
        TurnRequest request,
        IToolDispatcher dispatcher,
        Func<Exception, ModelError> mapException,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var options = new ChatOptions
        {
            ModelId = request.Model,
            Temperature = 0.2f,
            Tools = request.Tools
                .Select(tool => (AITool)new DispatcherAIFunction(tool, dispatcher, request.TurnId, logger))
                .ToList(),
            ToolMode = ChatToolMode.Auto,
        };

        ChatMessage[] messages =
        [
            new(ChatRole.System, request.SystemPrompt),
            new(ChatRole.User, request.UserPrompt),
        ];

        try
        {
            var response = await pipeline.Client
                .GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            return TurnResult.Done(
                ExtractText(response),
                ResolveToolCalls(response, dispatcher),
                MapUsage(response.Usage, pipeline.AllRoundsReportedUsage),
                ResolveChangedFiles(response, dispatcher));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停机或回合超时：交给上层统一映射为 user_cancelled / turn_timeout。
            throw;
        }
        catch (ToolInvocationException exception)
        {
            // 连续工具错误达到框架上限后，框架会把最后一次异常原样抛出。
            return TurnResult.Failed(
                new ModelError(exception.Kind, exception.Code, exception.Message),
                dispatcher.ExecutedCalls);
        }
        catch (ChatTurnException exception) when (exception.Error.Kind == ModelErrorKinds.UserCancelled)
        {
            return TurnResult.Cancelled(exception.Error, dispatcher.ExecutedCalls);
        }
        catch (OperationCanceledException)
        {
            return TurnResult.Failed(
                new ModelError(ModelErrorKinds.Transient, DevHubErrorCodes.RequestTimeout, "模型请求被取消。"),
                dispatcher.ExecutedCalls);
        }
        catch (Exception exception)
        {
            return TurnResult.Failed(mapException(exception), dispatcher.ExecutedCalls);
        }
    }

    /// <summary>取最后一条 assistant 消息的文本，避免把中间轮次（工具调用前）的说明拼进结果。</summary>
    /// <param name="response">模型响应。</param>
    private static string ExtractText(ChatResponse response) =>
        response.Messages.LastOrDefault(message => message.Role == ChatRole.Assistant)?.Text ?? string.Empty;

    /// <summary>桥报告的工具调用次数优先；否则使用执行器实际计数。</summary>
    /// <param name="response">模型响应。</param>
    /// <param name="dispatcher">工具执行器。</param>
    private static int ResolveToolCalls(ChatResponse response, IToolDispatcher dispatcher) =>
        response.AdditionalProperties is { } properties &&
        properties.TryGetValue(ToolCallsProperty, out var value) &&
        value is int count
            ? count
            : dispatcher.ExecutedCalls;

    /// <summary>桥报告的改动文件优先；否则使用执行器实际记录。</summary>
    /// <param name="response">模型响应。</param>
    /// <param name="dispatcher">工具执行器。</param>
    private static IReadOnlyList<string> ResolveChangedFiles(ChatResponse response, IToolDispatcher dispatcher) =>
        response.AdditionalProperties is { } properties &&
        properties.TryGetValue(FilesChangedProperty, out var value) &&
        value is IReadOnlyList<string> files
            ? files
            : dispatcher.ChangedFiles;

    /// <summary>
    /// 把 <see cref="UsageDetails"/> 收敛为 <see cref="TokenUsage"/>：
    /// 任意一轮缺失 usage，或累加值没有任何有效计数时，整回合用量视为不可信（返回 null）。
    /// </summary>
    /// <param name="usage">框架聚合后的用量。</param>
    /// <param name="allRoundsReportedUsage">是否每一轮都带真实 usage。</param>
    private static TokenUsage? MapUsage(UsageDetails? usage, bool allRoundsReportedUsage)
    {
        if (!allRoundsReportedUsage || !UsageTrackingChatClient.HasUsableCounts(usage))
        {
            return null;
        }

        return new TokenUsage(ToInt32(usage!.InputTokenCount), ToInt32(usage.OutputTokenCount), Estimated: false);
    }

    /// <summary>把 long? 计数收敛到 int 范围；null 按 0 处理（单边缺失保持既有语义）。</summary>
    /// <param name="value">token 计数。</param>
    private static int ToInt32(long? value) =>
        value is null ? 0 : (int)Math.Clamp(value.Value, 0, int.MaxValue);
}
