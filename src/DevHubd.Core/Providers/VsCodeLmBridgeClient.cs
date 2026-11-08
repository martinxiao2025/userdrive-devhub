using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Providers;

/// <summary>
/// <c>vscode</c> provider：把回合交给 VS Code 扩展的 <c>vscode.lm</c> 回合桥执行。
/// 发现走 <c>lm-bridge.json</c>，但**必须**再请求 <c>GET /health</c> 校验 pid 存活，
/// 不能只凭发现文件存在就判定可用（进程崩溃后文件会残留）。
/// 工具调用由扩展回调宿主的 <c>/internal/tools/call</c>，C# 侧通过 <see cref="IChatClient"/> 适配器等待回合终态。
/// </summary>
public sealed class VsCodeLmBridgeClient : IModelClient
{
    /// <summary>provider 标识，与 <c>queue_item.preferred_provider</c> 取值一致。</summary>
    public const string ProviderId = "vscode";

    /// <summary>具名 HttpClient：桥端口在运行时发现，地址用绝对 URL，因此不设 BaseAddress。</summary>
    public const string HttpClientName = "lm-bridge";

    private readonly HttpClient _http;
    private readonly DevHubPaths _paths;
    private readonly InternalApiOptions _internalApi;
    private readonly TurnDispatcherRegistry _turns;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<VsCodeLmBridgeClient> _logger;

    /// <summary>注入 HTTP 客户端工厂、路径、内部 API 配置、回合注册表、日志工厂与日志器。</summary>
    /// <param name="httpClientFactory">HTTP 客户端工厂（由宿主提供具名客户端）。</param>
    /// <param name="paths">运行时路径。</param>
    /// <param name="internalApi">内部 API 配置。</param>
    /// <param name="turns">回合注册表。</param>
    /// <param name="loggerFactory">日志工厂（用于脱敏中间件）。</param>
    /// <param name="logger">日志器。</param>
    public VsCodeLmBridgeClient(
        IHttpClientFactory httpClientFactory,
        DevHubPaths paths,
        InternalApiOptions internalApi,
        TurnDispatcherRegistry turns,
        ILoggerFactory loggerFactory,
        ILogger<VsCodeLmBridgeClient> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _paths = paths;
        _internalApi = internalApi;
        _turns = turns;
        _loggerFactory = loggerFactory;
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
    /// 执行一个回合：登记工具派发器 → 通过 <see cref="BridgeChatClient"/> 走 <c>POST /turns</c> + 轮询 → 注销派发器。
    /// 业务失败以 <see cref="TurnResult"/> 返回；取消令牌已取消时原样抛出 <see cref="OperationCanceledException"/>。
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
            return TurnResult.Failed(new ModelError(
                ModelErrorKinds.BridgeDown,
                DevHubErrorCodes.BridgeDown,
                readError ?? "扩展桥不可用"));
        }

        _turns.Register(request.TurnId, dispatcher);
        try
        {
            using var pipeline = ChatTurnExecutor.CreatePipeline(
                new BridgeChatClient(_http, discovery, _internalApi, request, _logger),
                request,
                dispatcher,
                _logger,
                client => new SanitizedLoggingChatClient(
                    client,
                    ProviderId,
                    _loggerFactory.CreateLogger<SanitizedLoggingChatClient>()));

            return await ChatTurnExecutor
                .RunAsync(pipeline, request, dispatcher, MapException, _logger, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _turns.Unregister(request.TurnId);
        }
    }

    /// <summary>provider 级异常映射：桥通信/解析错误统一收敛到稳定错误码。</summary>
    /// <param name="exception">底层异常。</param>
    private static ModelError MapException(Exception exception) => exception switch
    {
        ChatTurnException controlled => controlled.Error,
        ToolInvocationException tool => new ModelError(tool.Kind, tool.Code, tool.Message),
        HttpRequestException => new ModelError(
            ModelErrorKinds.BridgeDown,
            DevHubErrorCodes.BridgeDown,
            "扩展桥通信失败。"),
        JsonException => new ModelError(
            ModelErrorKinds.ModelError,
            DevHubErrorCodes.BridgeBadResponse,
            "扩展桥响应无法解析。"),
        OperationCanceledException => new ModelError(
            ModelErrorKinds.Transient,
            DevHubErrorCodes.RequestTimeout,
            "回合请求超时。"),
        _ => new ModelError(
            ModelErrorKinds.ModelError,
            DevHubErrorCodes.TurnException,
            "回合异常结束。"),
    };

    /// <summary>拼出桥基地址（仅回环）。</summary>
    /// <param name="discovery">发现信息。</param>
    private static string BaseUrl(LmBridgeDiscovery discovery) => $"http://127.0.0.1:{discovery.Port}";

    /// <summary>扩展 <c>/health</c> 的返回体。</summary>
    /// <param name="Ok">是否就绪。</param>
    /// <param name="Pid">宿主进程 pid。</param>
    /// <param name="Model">当前模型。</param>
    private sealed record BridgeHealth(bool Ok, int Pid, string? Model);
}
