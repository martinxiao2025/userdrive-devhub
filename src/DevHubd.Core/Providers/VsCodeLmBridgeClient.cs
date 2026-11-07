using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Providers;

/// <summary>
/// <c>vscode</c> provider：把回合交给 VS Code 扩展的 <c>vscode.lm</c> 回合桥执行。
/// 发现走 <c>lm-bridge.json</c>，但**必须**再请求 <c>GET /health</c> 校验 pid 存活，
/// 不能只凭发现文件存在就判定可用（进程崩溃后文件会残留）。
/// 工具调用由扩展回调宿主的 <c>/internal/tools/call</c>，C# 侧只等待回合终态。
/// </summary>
public sealed class VsCodeLmBridgeClient : IModelClient
{
    /// <summary>provider 标识，与 <c>queue_item.preferred_provider</c> 取值一致。</summary>
    public const string ProviderId = "vscode";

    private const int PollWaitSeconds = 30;

    /// <summary>与扩展交换 JSON 时的序列化约定：camelCase，与 <c>lm-bridge.json</c> 保持一致。</summary>
    private static readonly JsonSerializerOptions _bridgeJson = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly DevHubPaths _paths;
    private readonly InternalApiOptions _internalApi;
    private readonly TurnDispatcherRegistry _turns;
    private readonly ILogger<VsCodeLmBridgeClient> _logger;

    /// <summary>注入 HTTP 客户端、路径、内部 API 参数、回合注册表与日志器。</summary>
    /// <param name="http">HTTP 客户端（由宿主提供，不设默认超时）。</param>
    /// <param name="paths">运行时路径（含 <c>lm-bridge.json</c>）。</param>
    /// <param name="internalApi">宿主内部 API 的参数。</param>
    /// <param name="turns">活跃回合注册表。</param>
    /// <param name="logger">日志器。</param>
    public VsCodeLmBridgeClient(
        HttpClient http,
        DevHubPaths paths,
        InternalApiOptions internalApi,
        TurnDispatcherRegistry turns,
        ILogger<VsCodeLmBridgeClient> logger)
    {
        _http = http;
        _paths = paths;
        _internalApi = internalApi;
        _turns = turns;
        _logger = logger;
    }

    /// <summary>provider 标识。</summary>
    public string Provider => ProviderId;

    /// <summary>
    /// 探测扩展桥：读发现文件 → 请求 <c>/health</c> → 校验 pid 一致。
    /// 任一环节失败都返回 <c>down</c>，不打开熔断（启动探测的既有约定）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ModelHealth> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!LmBridgeDiscovery.TryRead(_paths.LmBridgeFilePath, out var discovery, out var readError) || discovery is null)
        {
            return new ModelHealth(false, ProviderStates.Down, readError, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl(discovery)}/health");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", discovery.Token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ModelHealth(false, ProviderStates.Auth, "扩展桥拒绝令牌（令牌可能已轮换，请重载窗口）", null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ModelHealth(false, ProviderStates.Down, $"扩展桥 /health 返回 {(int)response.StatusCode}", null);
            }

            var health = await response.Content.ReadFromJsonAsync<BridgeHealth>(cancellationToken).ConfigureAwait(false);
            if (health is null || health.Pid != discovery.Pid)
            {
                return new ModelHealth(
                    false,
                    ProviderStates.Down,
                    $"发现文件 pid={discovery.Pid} 与桥实际 pid={health?.Pid} 不一致，判定为残留文件",
                    null);
            }

            var model = health.Model ?? discovery.Model;
            return string.IsNullOrWhiteSpace(model)
                ? new ModelHealth(false, ProviderStates.NoModel, "扩展未选择任何语言模型", null)
                : new ModelHealth(true, ProviderStates.Ready, null, model);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            // 不吞异常：探测失败要把原因写进状态详情，供 scheduler_status 展示。
            _logger.LogWarning(exception, "vscode 桥探测失败：port={Port}", discovery.Port);
            return new ModelHealth(false, ProviderStates.Down, $"扩展桥不可达：{exception.GetType().Name}", null);
        }
    }

    /// <summary>
    /// 执行一个回合：登记工具派发器 → <c>POST /turns</c> → 轮询终态 → 注销派发器。
    /// 失败以 <see cref="TurnResult"/> 返回，不向调用方抛异常（超时/取消也归入返回值）。
    /// </summary>
    /// <param name="request">回合请求。</param>
    /// <param name="dispatcher">工具派发器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<TurnResult> RunTurnAsync(
        TurnRequest request,
        IToolDispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        if (!LmBridgeDiscovery.TryRead(_paths.LmBridgeFilePath, out var discovery, out var readError) || discovery is null)
        {
            return TurnResult.Failed(new ModelError(ModelErrorKinds.BridgeDown, "BRIDGE_DOWN", readError ?? "扩展桥不可用"));
        }

        _turns.Register(request.TurnId, dispatcher);
        var startedAt = DateTimeOffset.UtcNow;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);

        try
        {
            var payload = new BridgeTurnRequest(
                request.TurnId,
                request.SessionId,
                request.Model ?? discovery.Model,
                request.SystemPrompt,
                request.UserPrompt,
                request.Budget.MaxToolRounds,
                (int)request.Budget.MaxDuration.TotalSeconds,
                request.Tools.Select(tool => new BridgeTool(tool.Name, tool.Description, tool.InputSchemaJson)).ToArray(),
                new BridgeToolCallback(
                    $"{_internalApi.BaseUrl.TrimEnd('/')}/internal/tools/call",
                    _internalApi.Token));

            using var post = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl(discovery)}/turns")
            {
                Content = JsonContent.Create(payload, options: _bridgeJson),
            };
            post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", discovery.Token);

            using var accepted = await _http.SendAsync(post, deadline.Token).ConfigureAwait(false);
            if (!accepted.IsSuccessStatusCode)
            {
                return TurnResult.Failed(MapStatus(accepted.StatusCode));
            }

            return await PollAsync(discovery, request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelTurnAsync(discovery, request.TurnId).ConfigureAwait(false);
            return TurnResult.Cancelled(new ModelError(ModelErrorKinds.UserCancelled, "USER_CANCELLED", "回合被取消"));
        }
        catch (OperationCanceledException)
        {
            await CancelTurnAsync(discovery, request.TurnId).ConfigureAwait(false);
            _logger.LogWarning(
                "vscode 回合超时：turnId={TurnId} elapsedSeconds={Elapsed}",
                request.TurnId,
                (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
            return TurnResult.Failed(new ModelError(ModelErrorKinds.Transient, "TURN_TIMEOUT", "扩展桥回合超时"));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            _logger.LogError(exception, "vscode 回合失败：turnId={TurnId}", request.TurnId);
            return TurnResult.Failed(new ModelError(
                ModelErrorKinds.BridgeDown,
                "BRIDGE_DOWN",
                $"扩展桥通信失败：{exception.GetType().Name}"));
        }
        finally
        {
            _turns.Unregister(request.TurnId);
        }
    }

    /// <summary>轮询回合终态，直到 done / failed / cancelled 或预算超时。</summary>
    /// <param name="discovery">发现信息。</param>
    /// <param name="request">回合请求。</param>
    /// <param name="cancellationToken">带截止时间的令牌。</param>
    private async Task<TurnResult> PollAsync(
        LmBridgeDiscovery discovery,
        TurnRequest request,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var get = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl(discovery)}/turns/{request.TurnId}?wait={PollWaitSeconds}");
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", discovery.Token);

            using var response = await _http.SendAsync(get, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return TurnResult.Failed(MapStatus(response.StatusCode));
            }

            var status = await response.Content.ReadFromJsonAsync<BridgeTurnStatus>(cancellationToken).ConfigureAwait(false);
            if (status is null)
            {
                return TurnResult.Failed(new ModelError(ModelErrorKinds.ModelError, "BRIDGE_BAD_RESPONSE", "扩展桥返回空回合"));
            }

            switch (status.State)
            {
                case "done":
                    return TurnResult.Done(
                        status.Text ?? string.Empty,
                        status.ToolCalls,
                        status.InputTokens is null && status.OutputTokens is null
                            ? null
                            : new TokenUsage(status.InputTokens ?? 0, status.OutputTokens ?? 0, Estimated: true),
                        status.FilesChanged ?? Array.Empty<string>());

                case "failed":
                    return TurnResult.Failed(
                        new ModelError(
                            NormalizeKind(status.ErrorKind),
                            status.ErrorCode ?? "VSCODE_TURN_FAILED",
                            status.ErrorMessage ?? "扩展桥回合失败"),
                        status.ToolCalls);

                case "cancelled":
                    return TurnResult.Cancelled(
                        new ModelError(
                            ModelErrorKinds.UserCancelled,
                            status.ErrorCode ?? "USER_CANCELLED",
                            status.ErrorMessage ?? "回合被取消"),
                        status.ToolCalls);

                default:
                    // running：继续轮询，由 deadline 兜底。
                    continue;
            }
        }
    }

    /// <summary>尽力取消远端回合；失败只记 Debug，不影响本回合的结果语义。</summary>
    /// <param name="discovery">发现信息。</param>
    /// <param name="turnId">回合 id。</param>
    private async Task CancelTurnAsync(LmBridgeDiscovery discovery, string turnId)
    {
        try
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl(discovery)}/turns/{turnId}");
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", discovery.Token);
            using var response = await _http.SendAsync(delete, CancellationToken.None).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("取消 vscode 回合返回 {StatusCode}：turnId={TurnId}", (int)response.StatusCode, turnId);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "取消 vscode 回合失败：turnId={TurnId}", turnId);
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
    /// <param name="discovery">发现信息。</param>
    private static string BaseUrl(LmBridgeDiscovery discovery) => $"http://127.0.0.1:{discovery.Port}";

    /// <summary>扩展 <c>/health</c> 的返回体。</summary>
    /// <param name="Ok">是否就绪。</param>
    /// <param name="Pid">宿主进程 pid。</param>
    /// <param name="Model">当前模型。</param>
    private sealed record BridgeHealth(bool Ok, int Pid, string? Model);

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
    /// <param name="InputTokens">输入 token（估算）。</param>
    /// <param name="OutputTokens">输出 token（估算）。</param>
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
        string? ErrorKind,
        string? ErrorCode,
        string? ErrorMessage,
        IReadOnlyList<string>? FilesChanged);
}
