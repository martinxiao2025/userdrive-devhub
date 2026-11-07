using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DevHubd.Mcp;

/// <summary>
/// MCP 装配入口：宿主只调用这两个方法，不直接触碰 SDK 细节，
/// 以便 SDK 升级时改动被限制在 DevHubd.Mcp 内。
/// </summary>
public static class McpModule
{
    /// <summary>MCP 端点路径，`plugin/mcp.json` 必须与此保持一致。</summary>
    public const string EndpointPath = "/mcp";

    /// <summary>
    /// 注册无状态 streamable-http 的 MCP 服务，并扫描程序集内的 <c>[McpServerToolType]</c>。
    /// 无状态模式让守护进程重启后客户端无需感知旧会话。
    /// </summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddDevHubdMcp(this IServiceCollection services)
    {
        services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(McpModule).Assembly)
            .WithResourcesFromAssembly(typeof(McpModule).Assembly);

        return services;
    }

    /// <summary>
    /// 把 MCP 端点映射到宿主管道。
    /// </summary>
    /// <param name="app">宿主应用。</param>
    public static WebApplication MapDevHubdMcp(this WebApplication app)
    {
        app.MapMcp(EndpointPath);
        return app;
    }
}
