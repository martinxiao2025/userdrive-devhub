using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using OpenAI.Chat;

namespace DevHubd.Core.Providers;

/// <summary>
/// OpenAI 兼容端点客户端：BYOK 直连 <c>/chat/completions</c>，通过官方
/// <c>Microsoft.Extensions.AI</c> 适配器与 <c>FunctionInvokingChatClient</c> 完成工具循环。
/// 不依赖 VS Code 窗口，是定时任务 7×24 的前提。
/// 取消令牌已取消时原样抛出 <see cref="OperationCanceledException"/>；其余失败一律映射为 <see cref="TurnResult"/>。
/// </summary>
public sealed class OpenAiCompatibleModelClient : IModelClient
{
    /// <summary>provider 标识。</summary>
    public const string ProviderName = "openai";

    /// <summary>具名 HttpClient：BaseAddress / 超时 / User-Agent / 连接池在宿主组合根集中配置。</summary>
    public const string HttpClientName = "openai-chat";

    /// <summary>常见占位符取值；只用于探测阶段给出明确 detail，不阻断启动。</summary>
    private static readonly HashSet<string> _placeholderApiKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHANGE_ME",
        "CHANGEME",
        "REPLACE_ME",
        "YOUR_API_KEY",
        "<YOUR_API_KEY>",
        "PLACEHOLDER",
        "TODO",
    };

    private readonly HttpClient _http;
    private readonly OpenAiModelOptions _options;
    private readonly IApiKeyProvider _apiKeys;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OpenAiCompatibleModelClient> _logger;

    /// <summary>注入 HTTP 客户端工厂、配置、key 提供者、日志工厂与日志器。</summary>
    /// <param name="httpClientFactory">HTTP 客户端工厂（由宿主提供具名客户端）。</param>
    /// <param name="options">端点与模型配置；不含 key。</param>
    /// <param name="apiKeys">API key 提供者（仅环境变量）。</param>
    /// <param name="loggerFactory">日志工厂（用于脱敏中间件）。</param>
    /// <param name="logger">日志器。</param>
    public OpenAiCompatibleModelClient(
        IHttpClientFactory httpClientFactory,
        OpenAiModelOptions options,
        IApiKeyProvider apiKeys,
        ILoggerFactory loggerFactory,
        ILogger<OpenAiCompatibleModelClient> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _options = options;
        _apiKeys = apiKeys;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Provider => ProviderName;

    /// <inheritdoc />
    public Task<ModelHealth> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var apiKey = _apiKeys.GetApiKey();
        if (apiKey is null)
        {
            return Task.FromResult(new ModelHealth(
                false,
                ProviderStates.Auth,
                $"未配置环境变量 {_apiKeys.EnvironmentVariableName}",
                _options.Model));
        }

        if (IsPlaceholderApiKey(apiKey))
        {
            // 不阻断启动：真实调用仍会走 401 路径，但状态页能直接看出"还没换真实 key"。
            return Task.FromResult(new ModelHealth(
                false,
                ProviderStates.Auth,
                $"环境变量 {_apiKeys.EnvironmentVariableName} 仍是占位值，请替换为真实 key",
                _options.Model));
        }

        return Task.FromResult(new ModelHealth(true, ProviderStates.Ready, null, _options.Model));
    }

    /// <summary>判断取值是否明显是占位符：尖括号包裹，或命中常见占位词。</summary>
    /// <param name="apiKey">环境变量取值。</param>
    private static bool IsPlaceholderApiKey(string apiKey)
    {
        var value = apiKey.Trim();
        return _placeholderApiKeys.Contains(value) ||
            (value.StartsWith('<') && value.EndsWith('>'));
    }

    /// <inheritdoc />
    public async Task<TurnResult> RunTurnAsync(
        TurnRequest request,
        IToolDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var apiKey = _apiKeys.GetApiKey();
        if (apiKey is null)
        {
            return TurnResult.Failed(new ModelError(
                ModelErrorKinds.Auth,
                DevHubErrorCodes.MissingApiKey,
                $"未配置环境变量 {_apiKeys.EnvironmentVariableName}，无法调用模型。"));
        }

        var model = request.Model ?? _options.Model;

        // 把解析后的有效模型名回填到请求，确保 attempt.model 不会因为"用默认值"而落 NULL。
        var effectiveRequest = request with { Model = model };
        using var pipeline = CreatePipeline(model, apiKey, effectiveRequest, dispatcher);

        return await ChatTurnExecutor
            .RunAsync(pipeline, effectiveRequest, dispatcher, MapException, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 组合 provider 客户端：OpenAI SDK → IChatClient → FunctionInvokingChatClient → 脱敏日志中间件。
    /// SDK 自带重试与超时全部关闭，重试语义只由队列层决定。
    /// </summary>
    /// <param name="model">模型名。</param>
    /// <param name="apiKey">API key（只进 SDK 凭据对象）。</param>
    /// <param name="request">回合请求。</param>
    /// <param name="dispatcher">本次回合的工具执行器。</param>
    private ChatPipeline CreatePipeline(
        string model,
        string apiKey,
        TurnRequest request,
        IToolDispatcher dispatcher)
    {
        var sdkOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(_options.BaseUrl),
            NetworkTimeout = Timeout.InfiniteTimeSpan,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            Transport = new HttpClientPipelineTransport(_http, false, NullLoggerFactory.Instance),
        };

        var inner = new ChatClient(model, new ApiKeyCredential(apiKey), sdkOptions).AsIChatClient();

        return ChatTurnExecutor.CreatePipeline(
            inner,
            request,
            dispatcher,
            _logger,
            client => new SanitizedLoggingChatClient(
                client,
                ProviderName,
                _loggerFactory.CreateLogger<SanitizedLoggingChatClient>()));
    }

    /// <summary>provider 级异常映射：SDK/HTTP/反序列化错误统一收敛到稳定错误码。</summary>
    /// <param name="exception">底层异常。</param>
    private static ModelError MapException(Exception exception) => exception switch
    {
        ChatTurnException controlled => controlled.Error,
        ToolInvocationException tool => new ModelError(tool.Kind, tool.Code, tool.Message),
        ClientResultException client => MapClientResultException(client),
        HttpRequestException => new ModelError(
            ModelErrorKinds.Transient,
            DevHubErrorCodes.NetworkError,
            "模型端点不可达。"),
        JsonException => new ModelError(
            ModelErrorKinds.ModelError,
            DevHubErrorCodes.MalformedResponse,
            "模型响应无法解析。"),
        OperationCanceledException => new ModelError(
            ModelErrorKinds.Transient,
            DevHubErrorCodes.RequestTimeout,
            "模型请求超时。"),
        _ => new ModelError(
            ModelErrorKinds.ModelError,
            DevHubErrorCodes.TurnException,
            "模型回合异常结束。"),
    };

    /// <summary>把 SDK 的 HTTP 失败转换为错误分类。</summary>
    /// <param name="exception">SDK 抛出的客户端异常。</param>
    private static ModelError MapClientResultException(ClientResultException exception)
    {
        var body = exception.GetRawResponse()?.Content?.ToString() ?? string.Empty;
        return MapHttpError((HttpStatusCode)exception.Status, body);
    }

    /// <summary>
    /// HTTP 状态码 → 错误分类。配额/鉴权/无模型是 provider 级资源错误，必须与任务级瞬时错误区分开。
    /// </summary>
    /// <param name="status">HTTP 状态码。</param>
    /// <param name="body">响应体。</param>
    public static ModelError MapHttpError(HttpStatusCode status, string body)
    {
        var code = (int)status;
        var detail = ExtractErrorCode(body);

        if (code is 401 or 403)
        {
            return new ModelError(
                ModelErrorKinds.Auth,
                DevHubErrorCodes.AuthFailed,
                $"鉴权失败（HTTP {code}），请检查 API key。");
        }

        if (code == 429 || detail == "insufficient_quota")
        {
            return new ModelError(
                ModelErrorKinds.Quota,
                detail == "insufficient_quota" ? DevHubErrorCodes.QuotaExhausted : DevHubErrorCodes.RateLimited,
                $"该 provider 配额受限（HTTP {code}）。");
        }

        if (code == 404 || detail == "model_not_found")
        {
            return new ModelError(
                ModelErrorKinds.NoModel,
                DevHubErrorCodes.NoModel,
                $"模型不存在（HTTP {code}）。");
        }

        if (code is 408 or >= 500)
        {
            return new ModelError(
                ModelErrorKinds.Transient,
                DevHubErrorCodes.UpstreamError,
                $"模型服务暂时不可用（HTTP {code}）。");
        }

        return new ModelError(
            ModelErrorKinds.ModelError,
            DevHubErrorCodes.RequestRejected,
            $"模型请求被拒绝（HTTP {code}）。");
    }

    /// <summary>从错误响应体里提取 <c>error.code</c> 或 <c>error.type</c>。</summary>
    /// <param name="body">响应体。</param>
    private static string? ExtractErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    return code.GetString();
                }

                if (error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    return type.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体：按状态码分类即可。
        }

        return null;
    }
}
