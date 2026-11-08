using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DevHubd.Core;
using DevHubd.Core.Data;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using DevHubd.Core.Queue;
using DevHubd.Core.Results;
using DevHubd.Core.Roles;
using DevHubd.Core.Scheduling;
using DevHubd.Core.Tasks;
using DevHubd.Core.Workspaces;
using DevHubd.Endpoints;
using DevHubd.Hosting;
using DevHubd.Logging;
using DevHubd.Mcp;
using DevHubd.Mcp.Abstractions;
using DevHubd.Queue;
using DevHubd.Results;
using DevHubd.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevHubd;

/// <summary>
/// 进程入口。启动顺序即失败顺序：参数 → 单实例 → 端口 → 数据目录 → 迁移 → 宿主。
/// 任一步失败都写结构化日志并以约定退出码结束，不做静默降级。
/// 宿主建立之前的阶段使用引导日志工厂，保证"启动即失败"也有日志可查。
/// </summary>
internal static class Program
{
    /// <summary>启动并运行守护进程。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>进程退出码，取值见 <see cref="ExitCodes"/>。</returns>
    private static async Task<int> Main(string[] args)
    {
        // 控制台输出编码不属于日志：日志由 Microsoft.Extensions.Logging 负责。
        Console.OutputEncoding = Encoding.UTF8;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        using var loggerFactory = DevHubLogging.CreateBootstrapLoggerFactory(configuration);
        var logger = loggerFactory.CreateLogger("DevHubd.Program");
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

        DevHubdOptions options;
        try
        {
            options = DevHubdOptions.FromArgsAndEnvironment(args);
        }
        catch (ArgumentException exception)
        {
            DevHubLog.InvalidArguments(logger, exception);
            return Exit(logger, ExitCodes.InvalidArguments);
        }

        var paths = new DevHubPaths(options.DataDirectory);

        // 用户级配置要在启动日志之前加载：日志与 runtime.json 里的 deliveryMode
        // 必须是真正生效的配置值，而不是命令行解锁标记派生的值。
        var userConfig = DevHubUserConfig.Load(paths.UserConfigPath);
        foreach (var problem in userConfig.Problems)
        {
            DevHubLog.UserConfigProblem(logger, problem);
        }

        DevHubLog.Starting(
            logger,
            version,
            Environment.ProcessId,
            BuildMcpUrl(options),
            userConfig.Execution.DeliveryMode,
            paths.DataDirectory);

        using var guard = new SingleInstanceGuard(loggerFactory.CreateLogger<SingleInstanceGuard>());
        try
        {
            if (!guard.TryAcquire(paths, out _))
            {
                return Exit(logger, ExitCodes.InstanceAlreadyRunning);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 守卫需要先建数据目录；目录路径被文件占用或不可写时，按"数据目录不可写"退出。
            DevHubLog.DataDirectoryNotWritable(logger, paths.DataDirectory, exception);
            return Exit(logger, ExitCodes.DataDirectoryNotWritable);
        }

        if (!PortGuard.IsAvailable(options.BindAddress, options.McpPort))
        {
            DevHubLog.PortConflict(logger, options.McpPort, PortGuard.BuildConflictHint(options.McpPort));
            return Exit(logger, ExitCodes.PortInUse);
        }

        try
        {
            paths.EnsureWritable();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DevHubLog.DataDirectoryNotWritable(logger, paths.DataDirectory, exception);
            return Exit(logger, ExitCodes.DataDirectoryNotWritable);
        }

        // 先记下迁移前库文件是否存在：新库用配置档位做种子，老库以库内档位为准。
        var databaseExisted = File.Exists(paths.DatabasePath);

        // 角色声明是纯文件校验，放在迁移之前：声明坏了不留下任何数据库副作用。
        // 找不到插件包时只告警不阻断（行为与 v4 一致）；找到了就必须逐条通过校验。
        var pluginDirectory = RoleCatalog.ResolvePluginDirectory();
        RoleLoadResult? roleLoad = null;
        if (pluginDirectory is null)
        {
            DevHubLog.RoleCatalogMissing(
                logger,
                "设置 DEVHUB_PLUGIN_DIR 指向含 plugin.json 的目录，或从仓库内启动。");
        }
        else
        {
            roleLoad = RoleCatalog.Load(pluginDirectory);
            if (roleLoad.Errors.Count > 0)
            {
                foreach (var error in roleLoad.Errors)
                {
                    DevHubLog.RoleDeclarationInvalid(logger, error);
                }

                return Exit(logger, ExitCodes.InvalidPluginDeclaration);
            }
        }

        var factory = new SqliteConnectionFactory(paths.DatabasePath);

        int schemaVersion;
        try
        {
            schemaVersion = await Migrator.MigrateAsync(factory).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DevHubLog.MigrationFailed(logger, paths.DatabasePath, exception);
            return Exit(logger, ExitCodes.MigrationFailed);
        }

        DevHubLog.DatabaseReady(logger, paths.DatabasePath, schemaVersion);

        // P2：用户级配置已在启动日志之前加载完成，这里只做执行器依赖装配。
        var apiKeys = new EnvironmentApiKeyProvider(userConfig.OpenAi.ApiKeyEnvironmentVariable);
        DevHubLog.ExecutionConfigured(
            logger,
            paths.UserConfigPath,
            OpenAiCompatibleModelClient.ProviderName,
            userConfig.OpenAi.Model,
            apiKeys.EnvironmentVariableName,
            apiKeys.GetApiKey() is not null,
            userConfig.Execution.MaxParallel,
            userConfig.Execution.DeliveryMode);

        var startedAt = DateTimeOffset.UtcNow;
        var hostInfo = new HostInfo(startedAt, version, options, paths);

        // 内部 API 令牌：进程内随机生成，只写 runtime.json，不落库、不进日志。
        var apiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var internalApi = new InternalApiOptions($"http://{options.BindAddress}:{options.McpPort}", apiToken);

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

        // 运行时交付档位单例：配置只作初始值，随后由 scheduler_state.mode 校正。
        builder.Services.AddSingleton(new EffectiveDeliveryMode(userConfig.Execution.DeliveryMode));

        // 数据访问走 EF Core；连接串与裸连接工厂一致，PRAGMA 由拦截器在每次连接打开时执行。
        // 注意：不走 EF Migrations/Runtime（schema 由 Migrator 独占管理），只注册上下文工厂。
        builder.Services.AddDbContextFactory<DevHubDbContext>(dbOptions =>
        {
            dbOptions.UseSqlite(factory.ConnectionString);
            dbOptions.AddInterceptors(new SqlitePragmaInterceptor());
        });

        // P1：工作区清单按机器级数据目录读取，任务扫描、队列与调度状态全部复用同一份 SQLite。
        builder.Services.AddSingleton(new WorkspaceRegistry(paths.WorkspacesFilePath));
        builder.Services.AddSingleton(serviceProvider => new TaskScanner(
            serviceProvider.GetRequiredService<IDbContextFactory<DevHubDbContext>>()));
        builder.Services.AddSingleton(serviceProvider => new QueueService(
            serviceProvider.GetRequiredService<IDbContextFactory<DevHubDbContext>>()));
        builder.Services.AddSingleton(serviceProvider => new SchedulerStateStore(
            serviceProvider.GetRequiredService<IDbContextFactory<DevHubDbContext>>()));
        builder.Services.AddSingleton<SchedulerEngine>();
        builder.Services.AddSingleton<SchedulerWakeSignal>();

        // P2：执行闭环。HTTP 客户端统一用具名客户端：地址、超时、UA 与连接池只在这里配置。
        // 超时语义：HttpClient.Timeout = InfiniteTimeSpan，真正的时间预算由回合级 CancellationToken
        // 与 RequestTimeoutSeconds 控制，两者互不覆盖。
        var userAgent = $"devhubd/{version}";
        builder.Services.AddHttpClient(OpenAiCompatibleModelClient.HttpClientName, client =>
        {
            // BaseUrl 来自用户配置（末尾斜杠补齐），请求侧使用相对路径 chat/completions。
            client.BaseAddress = new Uri($"{userConfig.OpenAi.BaseUrl.TrimEnd('/')}/");
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 4,
        });

        builder.Services.AddHttpClient(VsCodeLmBridgeClient.HttpClientName, client =>
        {
            // 桥端口在运行时发现，地址是绝对 URL，不需要 BaseAddress。
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(3),
            MaxConnectionsPerServer = 2,
        });

        builder.Services.AddSingleton(userConfig.OpenAi);
        builder.Services.AddSingleton(userConfig.Execution);
        builder.Services.AddSingleton<IApiKeyProvider>(apiKeys);
        builder.Services.AddSingleton(internalApi);
        builder.Services.AddSingleton<IModelClient>(serviceProvider => new OpenAiCompatibleModelClient(
            serviceProvider.GetRequiredService<IHttpClientFactory>(),
            serviceProvider.GetRequiredService<OpenAiModelOptions>(),
            serviceProvider.GetRequiredService<IApiKeyProvider>(),
            serviceProvider.GetRequiredService<ILoggerFactory>(),
            serviceProvider.GetRequiredService<ILogger<OpenAiCompatibleModelClient>>()));
        builder.Services.AddSingleton<TurnDispatcherRegistry>();
        builder.Services.AddSingleton<IModelClient>(serviceProvider => new VsCodeLmBridgeClient(
            serviceProvider.GetRequiredService<IHttpClientFactory>(),
            serviceProvider.GetRequiredService<DevHubPaths>(),
            serviceProvider.GetRequiredService<InternalApiOptions>(),
            serviceProvider.GetRequiredService<TurnDispatcherRegistry>(),
            serviceProvider.GetRequiredService<ILoggerFactory>(),
            serviceProvider.GetRequiredService<ILogger<VsCodeLmBridgeClient>>()));
        builder.Services.AddSingleton<ModelClientRegistry>();
        builder.Services.AddSingleton(serviceProvider => new ProviderStateStore(
            serviceProvider.GetRequiredService<IDbContextFactory<DevHubDbContext>>()));
        builder.Services.AddSingleton(serviceProvider => new ExecutionStore(
            serviceProvider.GetRequiredService<IDbContextFactory<DevHubDbContext>>()));
        builder.Services.AddSingleton(serviceProvider => new ResultStore(
            serviceProvider.GetRequiredService<IDbContextFactory<DevHubDbContext>>()));
        builder.Services.AddSingleton(serviceProvider => new NoticeStore(
            serviceProvider.GetRequiredService<IDbContextFactory<DevHubDbContext>>()));
        builder.Services.AddSingleton(serviceProvider => new RoleSnapshotStore(
            serviceProvider.GetRequiredService<IDbContextFactory<DevHubDbContext>>()));
        builder.Services.AddSingleton<TurnRunner>();
        builder.Services.AddSingleton<ExecutionCoordinator>();

        builder.Services.AddSingleton<ISchedulerStatusSource>(serviceProvider => new DevHubHostState(
            serviceProvider.GetRequiredService<IDbContextFactory<DevHubDbContext>>(),
            serviceProvider.GetRequiredService<SchedulerStateStore>(),
            serviceProvider.GetRequiredService<HostInfo>()));
        builder.Services.AddSingleton<IQueueQuerySource, QueueQuerySource>();
        builder.Services.AddSingleton<IResultQuerySource, ResultQuerySource>();
        builder.Services.AddSingleton<IToolDigestSource, ToolDigestSource>();
        builder.Services.AddSingleton<ILimitsSource, LimitsSource>();
        builder.Services.AddSingleton<IDispatchService, DispatchService>();
        builder.Services.AddSingleton<ISchedulerControl, SchedulerControlService>();
        builder.Services.AddHostedService<SchedulerBackgroundService>();

        builder.Services.AddDevHubdMcp();

        var app = builder.Build();

        // M1b：scheduler_state.mode 是运行时档位的权威来源。
        // 新库把配置值写入种子；老库以库值为准，配置只作初始值（避免升级后被静默改写）。
        var stateStore = app.Services.GetRequiredService<SchedulerStateStore>();
        var effectiveDeliveryMode = app.Services.GetRequiredService<EffectiveDeliveryMode>();
        if (databaseExisted)
        {
            var persisted = await stateStore.ReadAsync().ConfigureAwait(false);
            effectiveDeliveryMode.Set(persisted.Mode);
            DevHubLog.DeliveryModeResolved(logger, effectiveDeliveryMode.Value, "scheduler_state");
        }
        else
        {
            await stateStore.SetModeAsync(effectiveDeliveryMode.Value).ConfigureAwait(false);
            DevHubLog.DeliveryModeResolved(logger, effectiveDeliveryMode.Value, "config");
        }

        // M6：只有显式传 --interval 才写回库，未传时沿用既有 interval_seconds。
        if (options.IntervalSpecified)
        {
            await stateStore.SetIntervalAsync(options.IntervalSeconds).ConfigureAwait(false);
            DevHubLog.IntervalPersisted(logger, options.IntervalSeconds);
        }

        // U7：把已校验的角色声明写入 role_view。快照是状态面与后续调度取角色的唯一来源，
        // 因此必须在开始接受 MCP 调用之前完成；内容哈希未变的角色不会重复写库。
        if (roleLoad is not null)
        {
            var roleStore = app.Services.GetRequiredService<RoleSnapshotStore>();
            var sync = await roleStore.SyncAsync(roleLoad.Roles).ConfigureAwait(false);
            DevHubLog.RoleSnapshotSynced(
                logger,
                roleLoad.Roles.Count,
                sync.Added,
                sync.Updated,
                sync.Removed,
                sync.Unchanged);
        }

        app.MapHealthEndpoints();
        app.MapInternalApiEndpoints();
        app.MapDevHubdMcp();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            DevHubLog.Listening(logger, BuildMcpUrl(options));

            try
            {
                RuntimeFileWriter.Write(
                    paths,
                    options,
                    effectiveDeliveryMode.Value,
                    startedAt,
                    apiToken);
                DevHubLog.RuntimeFileWritten(logger, paths.RuntimeFilePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                DevHubLog.RuntimeFileWriteFailed(logger, paths.RuntimeFilePath, exception);
            }
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            DevHubLog.Stopping(logger, hostInfo.UptimeSeconds);
            RuntimeFileWriter.Delete(paths, logger);
        });

        // 启动探测：把 provider 可用性落库，scheduler_status 在首个回合前即可见。
        await ProviderProbe.RunAsync(app.Services, logger).ConfigureAwait(false);

        try
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        catch (IOException exception) when (IsAddressInUse(exception))
        {
            // 预探测与实际绑定之间的竞态兜底。
            DevHubLog.PortConflict(logger, options.McpPort, PortGuard.BuildConflictHint(options.McpPort));
            return Exit(logger, ExitCodes.PortInUse);
        }

        return Exit(logger, ExitCodes.Success);
    }

    /// <summary>统一退出：先记录退出码再返回，保证每条退出路径都有日志。</summary>
    /// <param name="logger">进程级日志器。</param>
    /// <param name="exitCode">约定退出码。</param>
    /// <returns>原样返回退出码，便于 <c>return Exit(...)</c>。</returns>
    private static int Exit(ILogger logger, int exitCode)
    {
        DevHubLog.Exited(logger, exitCode);
        return exitCode;
    }

    /// <summary>拼出对外暴露的 MCP 端点 URL（仅回环）。</summary>
    /// <param name="options">启动选项。</param>
    private static string BuildMcpUrl(DevHubdOptions options) =>
        $"http://{options.BindAddress}:{options.McpPort}/mcp";

    /// <summary>判断异常链中是否包含"地址已占用"。</summary>
    /// <param name="exception">待检查的异常。</param>
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
