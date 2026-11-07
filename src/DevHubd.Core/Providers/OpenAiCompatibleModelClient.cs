using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Providers;

/// <summary>
/// OpenAI 兼容端点客户端：BYOK 直连 <c>/chat/completions</c>，在 C# 内跑"模型 → 工具 → 回填 → 再请求"的循环。
/// 不依赖 VS Code 窗口，是定时任务 7×24 的前提。
/// 失败一律以 <see cref="TurnResult"/> 返回；只有取消令牌触发时才向外传播 <see cref="OperationCanceledException"/>。
/// </summary>
public sealed class OpenAiCompatibleModelClient : IModelClient
{
    /// <summary>provider 标识。</summary>
    public const string ProviderName = "openai";

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

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
    private readonly ILogger<OpenAiCompatibleModelClient> _logger;

    /// <summary>注入 HTTP 客户端、配置、key 提供者与日志器。</summary>
    /// <param name="http">HTTP 客户端（超时由回合预算控制）。</param>
    /// <param name="options">端点与模型配置；不含 key。</param>
    /// <param name="apiKeys">API key 提供者（仅环境变量）。</param>
    /// <param name="logger">日志器。</param>
    public OpenAiCompatibleModelClient(
        HttpClient http,
        OpenAiModelOptions options,
        IApiKeyProvider apiKeys,
        ILogger<OpenAiCompatibleModelClient> logger)
    {
        _http = http;
        _options = options;
        _apiKeys = apiKeys;
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
                "MISSING_API_KEY",
                $"未配置环境变量 {_apiKeys.EnvironmentVariableName}，无法调用模型。"));
        }

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt },
        };

        var toolCalls = 0;
        var model = request.Model ?? _options.Model;

        for (var round = 1; round <= request.Budget.MaxToolRounds; round++)
        {
            var payload = BuildPayload(model, messages, request.Tools);

            JsonDocument document;
            try
            {
                document = await SendAsync(payload, apiKey, request.Timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (ModelCallException exception)
            {
                return TurnResult.Failed(exception.Error, toolCalls);
            }

            using (document)
            {
                var root = document.RootElement;
                if (!TryGetAssistantMessage(root, out var message))
                {
                    return TurnResult.Failed(new ModelError(
                        ModelErrorKinds.ModelError,
                        "MALFORMED_RESPONSE",
                        "模型响应缺少 choices[0].message。"), toolCalls);
                }

                var usage = ReadUsage(root);

                if (!message.TryGetProperty("tool_calls", out var calls) ||
                    calls.ValueKind != JsonValueKind.Array ||
                    calls.GetArrayLength() == 0)
                {
                    var text = message.TryGetProperty("content", out var content) &&
                               content.ValueKind == JsonValueKind.String
                        ? content.GetString() ?? string.Empty
                        : string.Empty;

                    _logger.LogInformation(
                        "模型回合完成：turnId={TurnId} provider={Provider} rounds={Rounds} toolCalls={ToolCalls}",
                        request.TurnId,
                        ProviderName,
                        round,
                        toolCalls);

                    return TurnResult.Done(text, toolCalls, usage, dispatcher.ChangedFiles);
                }

                messages.Add(CloneMessage(message));

                foreach (var call in calls.EnumerateArray())
                {
                    toolCalls++;
                    var toolName = call.TryGetProperty("function", out var function) &&
                                   function.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;

                    var arguments = function.ValueKind == JsonValueKind.Object &&
                                    function.TryGetProperty("arguments", out var argumentsElement)
                        ? argumentsElement.ToString()
                        : "{}";

                    var output = await ExecuteToolAsync(request.TurnId, toolName, arguments, dispatcher, cancellationToken)
                        .ConfigureAwait(false);

                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.TryGetProperty("id", out var id) ? id.GetString() : string.Empty,
                        ["content"] = output,
                    });
                }
            }
        }

        return TurnResult.Failed(new ModelError(
            ModelErrorKinds.BudgetExceeded,
            "TOOL_ROUNDS_EXCEEDED",
            $"工具轮数超过上限 {request.Budget.MaxToolRounds}。"), toolCalls);
    }

    /// <summary>执行一次工具调用；工具失败以文本回填给模型，由模型自行纠正。</summary>
    /// <param name="turnId">回合 id。</param>
    /// <param name="toolName">工具名。</param>
    /// <param name="arguments">参数 JSON 文本。</param>
    /// <param name="dispatcher">工具执行器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<string> ExecuteToolAsync(
        string turnId,
        string toolName,
        string arguments,
        IToolDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            var result = await dispatcher
                .ExecuteAsync(turnId, toolName, document.RootElement, cancellationToken)
                .ConfigureAwait(false);

            return result.Success
                ? result.Output ?? string.Empty
                : $"工具执行失败 [{result.ErrorCode}]：{result.ErrorMessage}";
        }
        catch (JsonException)
        {
            return "工具执行失败 [INVALID_ARGUMENT]：arguments 不是合法 JSON。";
        }
        finally
        {
            document?.Dispose();
        }
    }

    /// <summary>组装一次请求体。</summary>
    /// <param name="model">模型名。</param>
    /// <param name="messages">消息数组。</param>
    /// <param name="tools">工具白名单。</param>
    private static JsonObject BuildPayload(string model, JsonArray messages, IReadOnlyList<ToolDescriptor> tools)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            ["temperature"] = 0.2,
        };

        if (tools.Count > 0)
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.InputSchemaJson),
                    },
                });
            }

            payload["tools"] = toolArray;
            payload["tool_choice"] = "auto";
        }

        return payload;
    }

    /// <summary>发送请求并解析响应；HTTP 与网络错误统一映射为 <see cref="ModelCallException"/>。</summary>
    /// <param name="payload">请求体。</param>
    /// <param name="apiKey">API key（只进请求头）。</param>
    /// <param name="timeout">本回合最长时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<JsonDocument> SendAsync(
        JsonObject payload,
        string apiKey,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(_jsonOptions), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _http.SendAsync(message, timeoutSource.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ModelCallException(MapHttpError(response.StatusCode, body));
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new ModelCallException(new ModelError(
                    ModelErrorKinds.ModelError,
                    "MALFORMED_RESPONSE",
                    "模型响应不是合法 JSON。"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外部取消（停机）：交由执行器按 user_cancelled 处理。
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new ModelCallException(new ModelError(
                ModelErrorKinds.Transient,
                "REQUEST_TIMEOUT",
                $"模型请求超过 {timeout.TotalSeconds:0} 秒未完成。"));
        }
        catch (HttpRequestException exception)
        {
            throw new ModelCallException(new ModelError(
                ModelErrorKinds.Transient,
                "NETWORK_ERROR",
                $"模型端点不可达：{exception.Message}"));
        }
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
            return new ModelError(ModelErrorKinds.Auth, "AUTH_FAILED", $"鉴权失败（HTTP {code}），请检查 API key。");
        }

        if (code == 429 || detail == "insufficient_quota")
        {
            return new ModelError(
                ModelErrorKinds.Quota,
                detail == "insufficient_quota" ? "QUOTA_EXHAUSTED" : "RATE_LIMITED",
                $"该 provider 配额受限（HTTP {code}）。");
        }

        if (code == 404 || detail == "model_not_found")
        {
            return new ModelError(ModelErrorKinds.NoModel, "NO_MODEL", $"模型不存在（HTTP {code}）。");
        }

        if (code is 408 or >= 500)
        {
            return new ModelError(ModelErrorKinds.Transient, "UPSTREAM_ERROR", $"模型服务暂时不可用（HTTP {code}）。");
        }

        return new ModelError(ModelErrorKinds.ModelError, "REQUEST_REJECTED", $"模型请求被拒绝（HTTP {code}）。");
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

    /// <summary>读取 <c>choices[0].message</c>。</summary>
    /// <param name="root">响应根节点。</param>
    /// <param name="message">消息节点。</param>
    private static bool TryGetAssistantMessage(JsonElement root, out JsonElement message)
    {
        message = default;
        return root.TryGetProperty("choices", out var choices) &&
               choices.ValueKind == JsonValueKind.Array &&
               choices.GetArrayLength() > 0 &&
               choices[0].TryGetProperty("message", out message) &&
               message.ValueKind == JsonValueKind.Object;
    }

    /// <summary>读取 OpenAI 的 usage 字段。</summary>
    /// <param name="root">响应根节点。</param>
    private static TokenUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var promptValue)
            ? promptValue
            : 0;
        var output = usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var completionValue)
            ? completionValue
            : 0;

        return new TokenUsage(input, output, Estimated: false);
    }

    /// <summary>把 assistant 消息（含 tool_calls）原样回填，保持与请求的 tool_call_id 对应。</summary>
    /// <param name="message">assistant 消息节点。</param>
    private static JsonNode? CloneMessage(JsonElement message) =>
        JsonNode.Parse(message.GetRawText());

    /// <summary>把模型调用失败包装成受控异常，避免业务错误以任意异常形态逃逸。</summary>
    private sealed class ModelCallException : Exception
    {
        /// <summary>构造受控失败。</summary>
        /// <param name="error">错误描述。</param>
        public ModelCallException(ModelError error)
            : base(error.Message)
        {
            Error = error;
        }

        /// <summary>错误描述。</summary>
        public ModelError Error { get; }
    }
}
