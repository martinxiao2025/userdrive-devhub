using System.Diagnostics;
using DevHubd.Core.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Providers;

/// <summary>
/// 脱敏日志中间件：只记录 provider、模型、消息数、工具数、用量与耗时，
/// 不记录提示词、文件内容或工具参数（框架内部日志器已置空）。
/// </summary>
public sealed class SanitizedLoggingChatClient : DelegatingChatClient
{
    private readonly string _provider;
    private readonly ILogger _logger;

    /// <summary>包装底层客户端并记录摘要日志。</summary>
    /// <param name="innerClient">底层客户端。</param>
    /// <param name="provider">provider 标识。</param>
    /// <param name="logger">日志器。</param>
    public SanitizedLoggingChatClient(IChatClient innerClient, string provider, ILogger logger)
        : base(innerClient)
    {
        _provider = provider;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var model = options?.ModelId;

        ChatLog.RequestStarted(
            _logger,
            _provider,
            model,
            messageList.Count,
            options?.Tools?.Count ?? 0);

        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await base.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false);

            ChatLog.RequestCompleted(
                _logger,
                _provider,
                response.ModelId ?? model,
                response.FinishReason?.ToString(),
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ChatLog.RequestCancelled(_logger, _provider, model);
            throw;
        }
        catch (Exception exception)
        {
            ChatLog.RequestFailed(_logger, exception, _provider, model, exception.GetType().Name);
            throw;
        }
    }
}
