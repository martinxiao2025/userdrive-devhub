using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Results;
using DevHubd.Logging;
using Microsoft.Extensions.Logging;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace DevHubd.Endpoints;

/// <summary>
/// 宿主内部 API（仅回环）：供 VS Code 扩展桥在 <c>vscode.lm</c> 回合内回调执行工具，
/// 以及供扩展轮询未读结果通知。保护措施：只监听 127.0.0.1、Bearer 令牌来自 <c>runtime.json</c>
/// （进程内随机生成）、常量时间比较；令牌与工具参数都不写日志。
/// </summary>
public static class InternalApiEndpoints
{
    /// <summary>内部 API 路径前缀。</summary>
    public const string ToolsCallPath = "/internal/tools/call";

    /// <summary>未读通知轮询路径；支持 <c>sinceSeq</c> 与 <c>limit</c> 查询参数。</summary>
    public const string NotificationsPath = "/internal/notifications";

    /// <summary>通知确认路径：扩展弹窗后回写，保证崩溃重启不重复提醒。</summary>
    public const string NoticeAckPath = "/internal/notifications/ack";

    /// <summary>优雅停止路径：<c>devhub-daemon.ps1 stop</c> 使用，走宿主正常的关闭流程。</summary>
    public const string ShutdownPath = "/internal/shutdown";

    /// <summary>注册内部端点。</summary>
    /// <param name="app">宿主应用。</param>
    public static WebApplication MapInternalApiEndpoints(this WebApplication app)
    {
        app.MapPost(ToolsCallPath, HandleToolCallAsync);
        app.MapGet(NotificationsPath, HandleNotificationsAsync);
        app.MapPost(NoticeAckPath, HandleNoticeAckAsync);
        app.MapPost(ShutdownPath, HandleShutdown);
        return app;
    }

    /// <summary>
    /// 返回序号大于 <paramref name="sinceSeq"/> 且未确认的通知（最多 <paramref name="limit"/> 条），
    /// 并给出当前最大序号供首次轮询建立基线。去重以数据库为准，扩展侧内存去重只是二次保护。
    /// </summary>
    /// <param name="context">HTTP 上下文（取 Authorization 头）。</param>
    /// <param name="api">内部 API 参数。</param>
    /// <param name="store">结果存取。</param>
    /// <param name="notices">通知存取。</param>
    /// <param name="sinceSeq">已处理的最大序号；空表示首次轮询。</param>
    /// <param name="limit">最多返回条数；空取 3。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<IResult> HandleNotificationsAsync(
        HttpContext context,
        InternalApiOptions api,
        ResultStore store,
        NoticeStore notices,
        long? sinceSeq,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context, api.Token))
        {
            return HttpResults.Json(new { code = "UNAUTHORIZED", message = "内部 API 令牌无效。" }, statusCode: 401);
        }

        var effectiveLimit = limit ?? 3;
        if (effectiveLimit is < 1 or > 50)
        {
            return HttpResults.Json(
                new { code = "INVALID_LIMIT", message = "limit 必须在 1–50 之间。" },
                statusCode: 400);
        }

        var digest = await store.GetDigestAsync(cancellationToken).ConfigureAwait(false);
        var page = await notices.ListSinceAsync(sinceSeq ?? 0, effectiveLimit, cancellationToken)
            .ConfigureAwait(false);

        return HttpResults.Json(new
        {
            unread = digest.Unread,
            failures = digest.Failures,
            latestSeq = page.LatestSeq,
            items = page.Items.Select(item => new
            {
                seq = item.Seq,
                taskId = item.TaskId,
                kind = item.Kind,
                title = item.Title.Length <= 80 ? item.Title : item.Title[..80],
                createdAt = item.CreatedAt,
            }),
        });
    }

    /// <summary>确认已弹出的通知序号。</summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="api">内部 API 参数。</param>
    /// <param name="request">确认请求体。</param>
    /// <param name="notices">通知存取。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<IResult> HandleNoticeAckAsync(
        HttpContext context,
        InternalApiOptions api,
        NoticeAckRequest request,
        NoticeStore notices,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context, api.Token))
        {
            return HttpResults.Json(new { code = "UNAUTHORIZED", message = "内部 API 令牌无效。" }, statusCode: 401);
        }

        var acked = await notices
            .AckAsync(request.Seq ?? Array.Empty<long>(), DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
            .ConfigureAwait(false);

        DevHubLog.NoticeAcked(loggerFactory.CreateLogger("DevHubd.Endpoints.InternalApi"), acked);
        return HttpResults.Json(new { acked });
    }

    /// <summary>
    /// 请求宿主优雅停止。仅回环 + Bearer 令牌；停止流程由宿主统一处理
    /// （清理 runtime.json、释放锁与租约），脚本等待进程退出即可。
    /// </summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="api">内部 API 参数。</param>
    /// <param name="lifetime">宿主生命周期。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    private static IResult HandleShutdown(
        HttpContext context,
        InternalApiOptions api,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory)
    {
        if (!IsAuthorized(context, api.Token))
        {
            return HttpResults.Json(new { code = "UNAUTHORIZED", message = "内部 API 令牌无效。" }, statusCode: 401);
        }

        DevHubLog.ShutdownRequested(loggerFactory.CreateLogger("DevHubd.Endpoints.InternalApi"), Environment.ProcessId);
        lifetime.StopApplication();
        return HttpResults.Json(new { stopping = true, pid = Environment.ProcessId }, statusCode: 202);
    }

    /// <summary>执行一次工具回调。</summary>
    /// <param name="request">回调请求。</param>
    /// <param name="context">HTTP 上下文（取 Authorization 头）。</param>
    /// <param name="api">内部 API 参数。</param>
    /// <param name="turns">活跃回合注册表。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<IResult> HandleToolCallAsync(
        ToolCallRequest request,
        HttpContext context,
        InternalApiOptions api,
        TurnDispatcherRegistry turns,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("DevHubd.Endpoints.InternalApi");

        if (!IsAuthorized(context, api.Token))
        {
            logger.LogWarning("内部 API 拒绝未授权请求：path={Path}", ToolsCallPath);
            return HttpResults.Json(new { code = "UNAUTHORIZED", message = "内部 API 令牌无效。" }, statusCode: 401);
        }

        if (!turns.TryGet(request.TurnId, out var dispatcher) || dispatcher is null)
        {
            return HttpResults.Json(
                new { code = "UNKNOWN_TURN", message = "回合不存在或已结束。" },
                statusCode: 404);
        }

        var arguments = request.Arguments is { ValueKind: JsonValueKind.Object } value
            ? value
            : JsonDocument.Parse("{}").RootElement.Clone();

        var result = await dispatcher
            .ExecuteAsync(request.TurnId, request.Name, arguments, cancellationToken)
            .ConfigureAwait(false);

        logger.LogDebug(
            "内部工具回调：turnId={TurnId} tool={Tool} success={Success}",
            request.TurnId,
            request.Name,
            result.Success);

        return HttpResults.Json(new
        {
            content = result.Output,
            isError = !result.Success,
            code = result.ErrorCode,
            message = result.ErrorMessage,
            changedFiles = dispatcher.ChangedFiles,
        });
    }

    /// <summary>常量时间比较 Bearer 令牌。</summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="expected">期望令牌。</param>
    private static bool IsAuthorized(HttpContext context, string expected)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = header[prefix.Length..].Trim();
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>工具回调请求体。</summary>
    /// <param name="TurnId">回合 id。</param>
    /// <param name="Name">工具名。</param>
    /// <param name="Arguments">工具入参。</param>
    public sealed record ToolCallRequest(string TurnId, string Name, JsonElement Arguments);

    /// <summary>通知确认请求体。</summary>
    /// <param name="Seq">已弹出的通知序号。</param>
    public sealed record NoticeAckRequest(long[]? Seq);
}
