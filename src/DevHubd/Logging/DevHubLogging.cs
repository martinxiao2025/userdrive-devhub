using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevHubd.Logging;

/// <summary>
/// 日志装配。宿主建立之前（参数解析、单实例、端口探测、数据库迁移）使用同一份配置的引导日志工厂，
/// 保证"启动即失败"的路径也有结构化日志。
/// 级别与输出格式全部由配置驱动：<c>Logging:LogLevel</c> 控制级别，
/// <c>Logging:Console:FormatterName</c>（simple / json）控制格式，代码中不硬编码级别。
/// </summary>
internal static class DevHubLogging
{
    /// <summary>日志配置节名。</summary>
    public const string SectionName = "Logging";

    /// <summary>
    /// 按 <c>appsettings.json</c> + 环境变量创建引导日志工厂。调用方负责 Dispose（Dispose 会刷出缓冲）。
    /// </summary>
    /// <param name="configuration">已加载的配置根。</param>
    /// <returns>仅含控制台提供程序的日志工厂。</returns>
    public static ILoggerFactory CreateBootstrapLoggerFactory(IConfiguration configuration)
    {
        return LoggerFactory.Create(builder =>
        {
            // AddConfiguration 同时提供级别过滤与提供程序选项绑定（Logging:Console:*）。
            builder.AddConfiguration(configuration.GetSection(SectionName));
            builder.AddConsole();
        });
    }
}
