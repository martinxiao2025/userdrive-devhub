using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevHubd.Hosting;

/// <summary>
/// 启动时的 provider 探测：把可用性与"未配置"原因写入 <c>provider_state</c>，
/// 让 <c>scheduler_status</c> 在第一个回合之前就能给出可操作的信息。
/// 探测本身不打开熔断（缺 key 时只显示 auth，避免重置 key 后仍被冷却挡住）。
/// </summary>
public static class ProviderProbe
{
    /// <summary>探测已注册 provider，并补齐 vscode 的占位状态。</summary>
    /// <param name="services">根服务提供者。</param>
    /// <param name="logger">启动日志器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task RunAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var registry = services.GetRequiredService<ModelClientRegistry>();
        var store = services.GetRequiredService<ProviderStateStore>();
        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var provider in registry.Providers)
        {
            var client = registry.Get(provider);
            if (client is null)
            {
                continue;
            }

            var health = await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
            await store.SaveAsync(
                new ProviderStateRecord(
                    provider,
                    health.State,
                    health.Detail,
                    BreakerStates.Closed,
                    null,
                    null,
                    0,
                    now),
                cancellationToken).ConfigureAwait(false);

            if (!health.Available)
            {
                logger.LogWarning(
                    "provider 不可用：provider={Provider} state={State} detail={Detail}",
                    provider,
                    health.State,
                    health.Detail);
            }
        }

        if (await store.GetAsync("vscode", cancellationToken).ConfigureAwait(false) is null)
        {
            await store.SaveAsync(
                new ProviderStateRecord(
                    "vscode",
                    ProviderStates.Down,
                    "扩展未激活或未安装（未发现 lm-bridge.json）",
                    BreakerStates.Closed,
                    null,
                    null,
                    0,
                    now),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
