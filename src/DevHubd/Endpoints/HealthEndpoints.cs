using DevHubd.Core.Data;
using DevHubd.Core.Scheduling;
using DevHubd.Hosting;
using HttpResults = Microsoft.AspNetCore.Http.Results;
using DevHubd.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
    /// <param name="stateStore">调度状态存取（交付档位以库为准）。</param>
    /// <param name="contextFactory">EF Core 上下文工厂。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    private static async Task<IResult> HandleAsync(
        HostInfo host,
        SchedulerStateStore stateStore,
        IDbContextFactory<DevHubDbContext> contextFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            // 健康探针统一走 EF：CanConnectAsync 内部执行 SELECT 1，
            // 之后在同一连接上读取 user_version 与表清单（DDL 语义仍由 Migrator 提供）。
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (!await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("数据库不可用");
            }

            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = (SqliteConnection)db.Database.GetDbConnection();
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
                deliveryMode = state.Mode,
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
