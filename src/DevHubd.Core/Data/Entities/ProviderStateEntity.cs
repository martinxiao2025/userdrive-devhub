namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>provider_state</c> 的持久化模型：以 provider 为主键的单行状态。
/// </summary>
public sealed class ProviderStateEntity
{
    /// <summary>provider 标识（主键）。</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>ready / down / no_model / quota / auth。</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>状态说明；不得包含凭据。</summary>
    public string? Detail { get; set; }

    /// <summary>closed / open / half_open。</summary>
    public string Breaker { get; set; } = string.Empty;

    /// <summary>熔断原因。</summary>
    public string? BreakerReason { get; set; }

    /// <summary>熔断打开时间（ISO-8601）。</summary>
    public string? BreakerOpenedAt { get; set; }

    /// <summary>连续瞬时失败次数。</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>最近一次检查时间（ISO-8601）。</summary>
    public string CheckedAt { get; set; } = string.Empty;
}
