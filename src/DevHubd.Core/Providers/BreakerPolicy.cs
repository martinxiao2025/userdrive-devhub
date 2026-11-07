using DevHubd.Core.Models;

namespace DevHubd.Core.Providers;

/// <summary>熔断状态：closed / open / half_open。</summary>
public static class BreakerStates
{
    /// <summary>正常派发。</summary>
    public const string Closed = "closed";

    /// <summary>停止该 provider 的派发。</summary>
    public const string Open = "open";

    /// <summary>冷却到期，放行探测。</summary>
    public const string HalfOpen = "half_open";
}

/// <summary>provider 运行状态：ready / down / no_model / quota / auth。</summary>
public static class ProviderStates
{
    /// <summary>可用。</summary>
    public const string Ready = "ready";

    /// <summary>不可达（桥缺失、网络中断）。</summary>
    public const string Down = "down";

    /// <summary>模型不存在。</summary>
    public const string NoModel = "no_model";

    /// <summary>配额耗尽。</summary>
    public const string Quota = "quota";

    /// <summary>鉴权失败。</summary>
    public const string Auth = "auth";
}

/// <summary><c>provider_state</c> 一行的投影。</summary>
/// <param name="Provider">provider 标识。</param>
/// <param name="State">运行状态。</param>
/// <param name="Detail">说明。</param>
/// <param name="Breaker">熔断状态。</param>
/// <param name="BreakerReason">熔断原因。</param>
/// <param name="BreakerOpenedAt">熔断打开时间。</param>
/// <param name="ConsecutiveFailures">连续瞬时失败次数。</param>
/// <param name="CheckedAt">最近一次更新时间。</param>
public sealed record ProviderStateRecord(
    string Provider,
    string State,
    string? Detail,
    string Breaker,
    string? BreakerReason,
    string? BreakerOpenedAt,
    int ConsecutiveFailures,
    string CheckedAt);

/// <summary>
/// 熔断策略（纯函数，便于单测）：按 provider 隔离，配额/鉴权/无模型立即打开，
/// 瞬时失败连续 5 次打开；冷却 10 分钟后转 half_open 放行探测。
/// </summary>
public static class BreakerPolicy
{
    /// <summary>连续瞬时失败阈值。</summary>
    public const int TransientFailureThreshold = 5;

    /// <summary>熔断冷却时长。</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 判断当前是否允许派发。open 且冷却未到 → 不允许；冷却已到 → 返回 half_open 状态。
    /// </summary>
    /// <param name="record">当前状态；null 视为首次使用。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="effective">实际生效的熔断状态（可能是 half_open）。</param>
    public static bool IsDispatchAllowed(ProviderStateRecord? record, DateTimeOffset now, out string effective)
    {
        if (record is null)
        {
            effective = BreakerStates.Closed;
            return true;
        }

        if (string.Equals(record.Breaker, BreakerStates.Open, StringComparison.Ordinal))
        {
            if (DateTimeOffset.TryParse(record.BreakerOpenedAt, out var openedAt) &&
                now - openedAt >= Cooldown)
            {
                effective = BreakerStates.HalfOpen;
                return true;
            }

            effective = BreakerStates.Open;
            return false;
        }

        effective = record.Breaker;
        return true;
    }

    /// <summary>
    /// 把一个错误分类映射为"打开熔断"或"累计瞬时失败"。
    /// </summary>
    /// <param name="kind">错误分类。</param>
    /// <returns>true 表示立即打开熔断。</returns>
    public static bool OpensImmediately(string kind) =>
        kind is ModelErrorKinds.Quota or ModelErrorKinds.Auth or ModelErrorKinds.NoModel;

    /// <summary>
    /// 计算失败后的新状态。
    /// </summary>
    /// <param name="current">当前状态；null 表示首次。</param>
    /// <param name="kind">错误分类。</param>
    /// <param name="detail">说明。</param>
    /// <param name="now">当前时间。</param>
    public static ProviderStateRecord OnFailure(
        ProviderStateRecord? current,
        string kind,
        string detail,
        DateTimeOffset now)
    {
        var failures = (current?.ConsecutiveFailures ?? 0) + 1;

        if (OpensImmediately(kind))
        {
            return new ProviderStateRecord(
                current?.Provider ?? string.Empty,
                MapKindToState(kind),
                detail,
                BreakerStates.Open,
                kind,
                now.ToString("O"),
                failures,
                now.ToString("O"));
        }

        if (kind == ModelErrorKinds.BridgeDown)
        {
            return new ProviderStateRecord(
                current?.Provider ?? string.Empty,
                ProviderStates.Down,
                detail,
                current?.Breaker ?? BreakerStates.Closed,
                current?.BreakerReason,
                current?.BreakerOpenedAt,
                failures,
                now.ToString("O"));
        }

        var shouldOpen = failures >= TransientFailureThreshold;
        return new ProviderStateRecord(
            current?.Provider ?? string.Empty,
            ProviderStates.Down,
            detail,
            shouldOpen ? BreakerStates.Open : current?.Breaker ?? BreakerStates.Closed,
            shouldOpen ? kind : current?.BreakerReason,
            shouldOpen ? now.ToString("O") : current?.BreakerOpenedAt,
            failures,
            now.ToString("O"));
    }

    /// <summary>成功后的状态：恢复 ready 并关闭熔断。</summary>
    /// <param name="provider">provider 标识。</param>
    /// <param name="now">当前时间。</param>
    public static ProviderStateRecord OnSuccess(string provider, DateTimeOffset now) =>
        new(provider, ProviderStates.Ready, null, BreakerStates.Closed, null, null, 0, now.ToString("O"));

    /// <summary>把错误分类映射为 provider 运行状态。</summary>
    /// <param name="kind">错误分类。</param>
    private static string MapKindToState(string kind) => kind switch
    {
        ModelErrorKinds.Quota => ProviderStates.Quota,
        ModelErrorKinds.Auth => ProviderStates.Auth,
        ModelErrorKinds.NoModel => ProviderStates.NoModel,
        ModelErrorKinds.BridgeDown => ProviderStates.Down,
        _ => ProviderStates.Down,
    };
}
