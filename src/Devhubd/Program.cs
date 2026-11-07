using System.Net;
using System.Net.Sockets;
using System.Text;
using Devhubd.Core;
using Devhubd.Core.Data;
using Devhubd.Endpoints;
using Devhubd.Hosting;
using Devhubd.Mcp;
using Devhubd.Mcp.Abstractions;

namespace Devhubd;

/// <summary>
/// 进程入口。启动顺序即失败顺序：参数 → 单实例 → 端口 → 数据目录 → 迁移 → 宿主。
/// 任一步失败都写明确错误并以约定退出码结束，不做静默降级。
/// </summary>
internal static class Program
{
    /// <summary>启动并运行守护进程。</summary>
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        DevhubdOptions options;
        try
        {
            options = DevhubdOptions.FromArgsAndEnvironment(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[devhubd] 启动失败：参数错误。{ex.Message}");
            return ExitCodes.InvalidArguments;
        }

        var paths = new DevhubPaths(options.DataDirectory);

        using var guard = new SingleInstanceGuard();
        if (!guard.TryAcquire(paths, out var instanceError))
        {
            Console.Error.WriteLine(instanceError);
            return ExitCodes.InstanceAlreadyRunning;
        }

        if (!PortGuard.IsAvailable(options.BindAddress, options.McpPort))
        {
            Console.Error.WriteLine(PortGuard.BuildConflictMessage(options.McpPort));
            return ExitCodes.PortInUse;
        }

        try
        {
            paths.EnsureWritable();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"[devhubd] 启动失败：数据目录不可写。\n" +
                $"目录：{paths.DataDirectory}\n" +
                $"原因：{ex.Message}\n" +
                "排查：确认目录存在、当前用户有写权限，或用 --data-dir 指定可写目录。");
            return ExitCodes.DataDirectoryNotWritable;
        }

        var factory = new SqliteConnectionFactory(paths.DatabasePath);
        try
        {
            var schemaVersion = await Migrator.MigrateAsync(factory).ConfigureAwait(false);
            Console.WriteLine($"[devhubd] 数据库就绪：{paths.DatabasePath}（schema v{schemaVersion}）");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[devhubd] 启动失败：数据库迁移失败。\n" +
                $"数据库：{paths.DatabasePath}\n" +
                $"原因：{ex.GetType().Name}: {ex.Message}\n" +
                "排查：备份后删除 devhub.db* 可重建（会丢失队列历史）。");
            return ExitCodes.MigrationFailed;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var hostInfo = new HostInfo(startedAt, version, options, paths);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Parse(options.BindAddress), options.McpPort));

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton(hostInfo);
        builder.Services.AddSingleton<ISchedulerStatusSource, DevhubHostState>();
        builder.Services.AddDevhubMcp();

        var app = builder.Build();

        app.MapHealthEndpoints();
        app.MapDevhubMcp();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                RuntimeFileWriter.Write(paths, options, startedAt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                app.Logger.LogWarning(ex, "runtime.json 写入失败，扩展将无法发现本实例");
            }
        });

        app.Lifetime.ApplicationStopping.Register(() => RuntimeFileWriter.Delete(paths));

        try
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        catch (IOException ex) when (IsAddressInUse(ex))
        {
            // 预探测与实际绑定之间的竞态兜底。
            Console.Error.WriteLine(PortGuard.BuildConflictMessage(options.McpPort));
            return ExitCodes.PortInUse;
        }

        return ExitCodes.Success;
    }

    /// <summary>判断异常链中是否包含"地址已占用"。</summary>
    private static bool IsAddressInUse(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return true;
            }
        }

        return false;
    }
}
