using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Logging;
using DevHubd.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Providers;

/// <summary>
/// <see cref="IChatClient"/> 适配器：一次调用对应扩展侧的一整回合（扩展自行跑工具循环）。
/// 只负责 <c>POST /turns</c> + 轮询终态，不重复实现循环、超时或重试；
/// 取消令牌已取消时尽力取消远端回合后原样抛出 <see cref="OperationCanceledException"/>。
/// </summary>
internal sealed class BridgeChatClient : IChatClient
{
    private const int PollWaitSeconds = 30;

    /// <summary>取消远端回合的最长等待时间；桥无响应时不能让取消请求永久挂起。</summary>
    private static readonly TimeSpan _cancelTimeout = TimeSpan.FromSeconds(3);

    /// <summary>与扩展交换 JSON 时的序列化约定：camelCase，与 <c>lm-bridge.json</c> 保持一致。</summary>
    private static readonly JsonSerializerOptions _bridgeJson = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LmBridgeDiscovery _discovery;
    private readonly InternalApiOptions _internalApi;
    private readonly TurnRequest _request;
    private readonly ILogger _logger;

    /// <summary>绑定一次回合的桥连接参数。</summary>
    /// <param name="http">具名 HttpClient（桥端口运行时发现，使用绝对地址）。</param>
    /// <param name="discovery">发现文件内容。</param>
    /// <param name="internalApi">宿主内部工具回调地址与令牌。</param>
    /// <param name="request">回合请求。</param>
    /// <param name="logger">日志器。</param>
    public BridgeChatClient(
        HttpClient http,
        LmBridgeDiscovery discovery,
        InternalApiOptions internalApi,
        TurnRequest request,
        ILogger logger)
    {
        _http = http;
        _discovery = discovery;
        _internalApi = internalApi;
        _request = request;
        _logger = logger;
    }

    /// <summary>
    /// 执行一次桥回合。提示词与工具来自 <see cref="TurnRequest"/>（执行器用同一份数据构造消息），
    /// 因此不解析 <paramref name="messages"/>。
    /// </summary>
    /// <param name="messages">消息（契约占位，桥按整回合交互）。</param>
    /// <param name="options">模型选项（契约占位）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = messages;
        _ = options;
        return ExecuteAsync(cancellationToken);
    }

    /// <summary>桥按整回合返回，不支持流式响应。</summary>
    /// <param name="messages">消息。</param>
    /// <param name="options">模型选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("vscode 桥按整回合返回，不支持流式响应。");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(IChatClient) ? this : null;

    /// <inheritdoc />
    public void Dispose()
    {
        // 桥适配器不持有可释放资源；HttpClient 生命周期由宿主工厂管理。
    }

    /// <summary>发送回合请求并轮询终态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ChatResponse> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var payload = new BridgeTurnRequest(
                _request.TurnId,
                _request.SessionId,
                _request.Model ?? _discovery.Model,
                _request.SystemPrompt,
                _request.UserPrompt,
                _request.Budget.MaxToolRounds,
                (int)_request.Budget.MaxDuration.TotalSeconds,
                _request.Tools
                    .Select(tool => new BridgeTool(tool.Name, tool.Description, tool.InputSchemaJson))
                    .ToArray(),
                new BridgeToolCallback(
                    $"{_internalApi.BaseUrl.TrimEnd('/')}/internal/tools/call",
                    _internalApi.Token));

            using var post = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl()}/turns")
            {
                Content = JsonContent.Create(payload, options: _bridgeJson),
            };
            post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _discovery.Token);

            using var accepted = await _http.SendAsync(post, cancellationToken).ConfigureAwait(false);
            if (!accepted.IsSuccessStatusCode)
            {
                throw new ChatTurnException(MapStatus(accepted.StatusCode));
            }

            return await PollAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelTurnAsync().ConfigureAwait(false);
            throw;
        }
        catch (ChatTurnException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            ChatLog.BridgeTurnFailed(_logger, exception, _request.TurnId, DevHubErrorCodes.BridgeDown);
            throw new ChatTurnException(new ModelError(
                ModelErrorKinds.BridgeDown,
                DevHubErrorCodes.BridgeDown,
                "扩展桥通信失败。"));
        }
    }

    /// <summary>轮询回合终态，直到 done / failed / cancelled；running 由回合取消令牌兜底。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ChatResponse> PollAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var get = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl()}/turns/{_request.TurnId}?wait={PollWaitSeconds}");
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _discovery.Token);

            using var response = await _http.SendAsync(get, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatTurnException(MapStatus(response.StatusCode));
            }

            var status = await response.Content
                .ReadFromJsonAsync<BridgeTurnStatus>(cancellationToken)
                .ConfigureAwait(false);

            if (status is null)
            {
                throw new ChatTurnException(new ModelError(
                    ModelErrorKinds.ModelError,
                    DevHubErrorCodes.BridgeBadResponse,
                    "扩展桥返回空回合。"));
            }

            switch (status.State)
            {
                case "done":
                    return BuildResponse(status);

                case "failed":
                    throw new ChatTurnException(new ModelError(
                        NormalizeKind(status.ErrorKind),
                        status.ErrorCode ?? "VSCODE_TURN_FAILED",
                        status.ErrorMessage ?? "扩展桥回合失败"));

                case "cancelled":
                    throw new ChatTurnException(new ModelError(
                        ModelErrorKinds.UserCancelled,
                        status.ErrorCode ?? "USER_CANCELLED",
                        status.ErrorMessage ?? "回合被取消"));

                default:
                    // running：继续轮询。
                    continue;
            }
        }
    }

    /// <summary>把桥的终态转换为统一的 <see cref="ChatResponse"/>（用量仅在 provider 来源时采信）。</summary>
    /// <param name="status">桥返回的终态。</param>
    private ChatResponse BuildResponse(BridgeTurnStatus status)
    {
        var usage = string.Equals(status.UsageSource, "provider", StringComparison.Ordinal) &&
                    (status.InputTokens is not null || status.OutputTokens is not null)
            ? new UsageDetails
            {
                InputTokenCount = status.InputTokens,
                OutputTokenCount = status.OutputTokens,
            }
            : null;

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, status.Text ?? string.Empty))
        {
            ModelId = _request.Model ?? _discovery.Model,
            Usage = usage,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ChatTurnExecutor.ToolCallsProperty] = status.ToolCalls,
                [ChatTurnExecutor.FilesChangedProperty] = status.FilesChanged ?? Array.Empty<string>(),
            },
        };
    }

    /// <summary>尽力取消远端回合；失败或超时只记 Debug，不影响本回合的取消语义。</summary>
    private async Task CancelTurnAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(_cancelTimeout);
            using var delete = new HttpRequestMessage(
                HttpMethod.Delete,
                $"{BaseUrl()}/turns/{_request.TurnId}");
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _discovery.Token);
            using var response = await _http.SendAsync(delete, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "取消 vscode 回合返回 {StatusCode}：turnId={TurnId}",
                    (int)response.StatusCode,
                    _request.TurnId);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            ChatLog.BridgeCancelFailed(_logger, exception, _request.TurnId);
        }
    }

    /// <summary>把扩展返回的错误分类收敛到 <see cref="ModelErrorKinds"/> 的取值集合。</summary>
    /// <param name="kind">扩展上报的分类。</param>
    private static string NormalizeKind(string? kind) => kind switch
    {
        ModelErrorKinds.Quota => ModelErrorKinds.Quota,
        ModelErrorKinds.Auth => ModelErrorKinds.Auth,
        ModelErrorKinds.NoModel => ModelErrorKinds.NoModel,
        ModelErrorKinds.BridgeDown => ModelErrorKinds.BridgeDown,
        ModelErrorKinds.BudgetExceeded => ModelErrorKinds.BudgetExceeded,
        ModelErrorKinds.UserCancelled => ModelErrorKinds.UserCancelled,
        ModelErrorKinds.Transient => ModelErrorKinds.Transient,
        _ => ModelErrorKinds.ModelError,
    };

    /// <summary>把 HTTP 状态码映射为错误分类，保持与 OpenAI 路径的口径一致。</summary>
    /// <param name="status">HTTP 状态码。</param>
    private static ModelError MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new ModelError(ModelErrorKinds.Auth, "BRIDGE_UNAUTHORIZED", "扩展桥拒绝访问（令牌失效或未授权语言模型）"),
        HttpStatusCode.TooManyRequests =>
            new ModelError(ModelErrorKinds.Quota, "BRIDGE_QUOTA", "扩展桥报告配额不足"),
        HttpStatusCode.NotFound =>
            new ModelError(ModelErrorKinds.BridgeDown, "BRIDGE_NOT_FOUND", "扩展桥端点不存在（扩展版本过旧）"),
        HttpStatusCode.RequestTimeout =>
            new ModelError(ModelErrorKinds.Transient, "BRIDGE_TIMEOUT", "扩展桥请求超时"),
        _ => new ModelError(ModelErrorKinds.Transient, "BRIDGE_ERROR", $"扩展桥返回 {(int)status}"),
    };

    /// <summary>拼出桥基地址（仅回环）。</summary>
    private string BaseUrl() => $"http://127.0.0.1:{_discovery.Port}";

    /// <summary>发给扩展的回合请求。</summary>
    /// <param name="TurnId">回合 id。</param>
    /// <param name="SessionId">会话 id。</param>
    /// <param name="Model">模型名。</param>
    /// <param name="SystemPrompt">系统提示词。</param>
    /// <param name="UserPrompt">用户指令。</param>
    /// <param name="MaxRounds">工具轮数上限。</param>
    /// <param name="TimeoutSeconds">回合超时秒数。</param>
    /// <param name="Tools">工具白名单。</param>
    /// <param name="ToolCallback">工具回调地址与令牌。</param>
    private sealed record BridgeTurnRequest(
        string TurnId,
        string SessionId,
        string? Model,
        string SystemPrompt,
        string UserPrompt,
        int MaxRounds,
        int TimeoutSeconds,
        IReadOnlyList<BridgeTool> Tools,
        BridgeToolCallback ToolCallback);

    /// <summary>发给扩展的工具描述。</summary>
    /// <param name="Name">工具名。</param>
    /// <param name="Description">说明。</param>
    /// <param name="InputSchema">JSON Schema 文本。</param>
    private sealed record BridgeTool(string Name, string Description, string InputSchema);

    /// <summary>工具回调地址与令牌。</summary>
    /// <param name="Url">宿主内部 API 地址。</param>
    /// <param name="Token">Bearer 令牌。</param>
    private sealed record BridgeToolCallback(string Url, string Token);

    /// <summary>扩展返回的回合状态。</summary>
    /// <param name="State">running / done / failed / cancelled。</param>
    /// <param name="Text">模型文本。</param>
    /// <param name="ToolCalls">工具调用次数。</param>
    /// <param name="InputTokens">输入 token；扩展拿不到真实 usage 时为 null。</param>
    /// <param name="OutputTokens">输出 token；扩展拿不到真实 usage 时为 null。</param>
    /// <param name="UsageSource">provider 表示真实 usage；none 表示未获得。</param>
    /// <param name="ErrorKind">错误分类。</param>
    /// <param name="ErrorCode">错误码。</param>
    /// <param name="ErrorMessage">错误说明。</param>
    /// <param name="FilesChanged">写入的文件。</param>
    private sealed record BridgeTurnStatus(
        string State,
        string? Text,
        int ToolCalls,
        int? InputTokens,
        int? OutputTokens,
        string? UsageSource,
        string? ErrorKind,
        string? ErrorCode,
        string? ErrorMessage,
        IReadOnlyList<string>? FilesChanged);
}
