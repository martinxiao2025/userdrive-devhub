using DevHubd.Core.Data;
using DevHubd.Hosting;
using HttpResults = Microsoft.AspNetCore.Http.Results;
using DevHubd.Logging;
using Microsoft.Extensions.Logging;

namespace DevHubd.Endpoints;

/// <summary>
/// 健康端点：`/health` 与 `/api/health`，用于扩展探测与人工排障。
/// 返回进程、端口、数据库版本与表清单；数据库不可用时返回 503 并记录带上下文的异常日志。
/// </summary>
public static class HealthEndpoints
{
    /// <summary>健康检查的日志类别。</summary>
    private const string LogCategory = "DevHubd.Endpoints.Health";

    /// <summary>注册健康端点。</summary>
    /// <param name="app">宿主应用。</param>
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", HandleAsync);
        app.MapGet("/api/health", HandleAsync);
        return app;
    }

    /// <summary>健康检查处理逻辑：探一次数据库并返回结构化的状态。</summary>
    /// <param name="host">宿主信息。</param>
    /// <param name="factory">SQLite 连接工厂。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    private static async Task<IResult> HandleAsync(
        HostInfo host,
        SqliteConnectionFactory factory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var version = await Migrator.GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            var tables = await Migrator.GetTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);

            return HttpResults.Json(new
            {
                status = "ok",
                pid = Environment.ProcessId,
                version = host.Version,
                startedAt = host.StartedAt.ToString("O"),
                uptimeSeconds = host.UptimeSeconds,
                mcp = new { url = $"http://{host.Options.BindAddress}:{host.Options.McpPort}/mcp", port = host.Options.McpPort },
                deliveryMode = host.Options.DeliveryMode,
                database = new { path = host.Paths.DatabasePath, schemaVersion = version, tables },
            });
        }
        catch (Exception exception)
        {
            DevHubLog.HealthDegraded(loggerFactory.CreateLogger(LogCategory), host.Paths.DatabasePath, exception);

            return HttpResults.Json(new
            {
                status = "degraded",
                pid = Environment.ProcessId,
                version = host.Version,
                error = exception.GetType().Name,
                message = "数据库不可用",
                database = new { path = host.Paths.DatabasePath },
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
