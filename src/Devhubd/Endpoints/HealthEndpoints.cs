using Devhubd.Core.Data;
using Devhubd.Hosting;

namespace Devhubd.Endpoints;

/// <summary>
/// 健康端点：`/health` 与 `/api/health`，用于扩展探测与人工排障。
/// 返回进程、端口、数据库版本与表清单；数据库不可用时返回 503。
/// </summary>
public static class HealthEndpoints
{
    /// <summary>注册健康端点。</summary>
    /// <param name="app">宿主应用。</param>
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", HandleAsync);
        app.MapGet("/api/health", HandleAsync);
        return app;
    }

    /// <summary>健康检查处理逻辑：探一次数据库并返回结构化的状态。</summary>
    private static async Task<IResult> HandleAsync(
        HostInfo host,
        SqliteConnectionFactory factory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var version = await Migrator.GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            var tables = await Migrator.GetTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);

            return Results.Json(new
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
        catch (Exception ex)
        {
            return Results.Json(new
            {
                status = "degraded",
                pid = Environment.ProcessId,
                version = host.Version,
                error = ex.GetType().Name,
                message = "数据库不可用",
                database = new { path = host.Paths.DatabasePath },
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
