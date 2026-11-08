using System.Globalization;
using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Scheduling;

/// <summary>单例调度状态（<c>scheduler_state</c> 投影）。</summary>
/// <param name="Enabled">是否启用扫描与入队。</param>
/// <param name="Mode">交付档位：auto / dispatch_only。</param>
/// <param name="PauseReason">暂停原因。</param>
/// <param name="IntervalSeconds">扫描间隔秒数。</param>
/// <param name="LastScanAt">上次扫描完成时间。</param>
/// <param name="NextScanAt">下次扫描时间。</param>
/// <param name="Counters">累计计数。</param>
public sealed record SchedulerState(
    bool Enabled,
    string Mode,
    string? PauseReason,
    int IntervalSeconds,
    string? LastScanAt,
    string? NextScanAt,
    IReadOnlyDictionary<string, long> Counters);

/// <summary>
/// 调度状态存取：读写 <c>scheduler_state</c> 单例行，并把累计计数以 JSON 落库，
/// 让"进程重启"不会把观测值清零。
/// 读写统一通过 EF Core（<see cref="DevHubDbContext"/>）完成。
/// </summary>
public sealed class SchedulerStateStore
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public SchedulerStateStore(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>用裸连接工厂构造（测试与不依赖 DI 的场景）。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public SchedulerStateStore(SqliteConnectionFactory factory)
        : this(new DevHubDbContextFactory(factory.ConnectionString))
    {
    }

    /// <summary>读取当前状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">缺少 id=1 的单例行。</exception>
    public async Task<SchedulerState> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.SchedulerStates
            .AsNoTracking()
            .Where(state => state.Id == 1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            throw new InvalidOperationException("scheduler_state 缺少 id=1 的行，迁移可能未完成。");
        }

        return new SchedulerState(
            row.Enabled,
            row.Mode,
            row.PauseReason,
            row.IntervalSeconds,
            row.LastScanAt,
            row.NextScanAt,
            ParseCounters(row.Counters));
    }

    /// <summary>写入一次扫描的时间戳与计数增量。</summary>
    /// <param name="lastScanAt">本次扫描完成时间。</param>
    /// <param name="nextScanAt">下次扫描时间。</param>
    /// <param name="deltas">计数增量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SaveScanAsync(
        string lastScanAt,
        string nextScanAt,
        IReadOnlyDictionary<string, long> deltas,
        CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var counters = new Dictionary<string, long>(current.Counters, StringComparer.Ordinal);

        foreach (var (key, value) in deltas)
        {
            counters[key] = counters.GetValueOrDefault(key) + value;
        }

        var countersJson = JsonSerializer.Serialize(counters);
        var now = Now();

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.SchedulerStates
            .Where(state => state.Id == 1)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(state => state.LastScanAt, lastScanAt)
                    .SetProperty(state => state.NextScanAt, nextScanAt)
                    .SetProperty(state => state.Counters, countersJson)
                    .SetProperty(state => state.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>启用或暂停调度。</summary>
    /// <param name="enabled">目标状态。</param>
    /// <param name="reason">暂停原因；恢复时传 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SetEnabledAsync(bool enabled, string? reason, CancellationToken cancellationToken = default)
    {
        var now = Now();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.SchedulerStates
            .Where(state => state.Id == 1)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(state => state.Enabled, enabled)
                    .SetProperty(state => state.PauseReason, reason)
                    .SetProperty(state => state.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 切换交付档位。只接受 <c>auto</c> 与 <c>dispatch_only</c>；
    /// 是否允许提升为 <c>auto</c> 由调用方（控制面）结合启动参数判定。
    /// </summary>
    /// <param name="mode">目标档位。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentException">档位不在允许集合内。</exception>
    public async Task SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(mode, "auto", StringComparison.Ordinal) &&
            !string.Equals(mode, "dispatch_only", StringComparison.Ordinal))
        {
            throw new ArgumentException($"未知交付档位：{mode}", nameof(mode));
        }

        var now = Now();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.SchedulerStates
            .Where(state => state.Id == 1)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(state => state.Mode, mode)
                    .SetProperty(state => state.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 写回扫描间隔（<c>--interval</c> 显式指定时使用）。
    /// 未显式指定时不会调用本方法，库值保持原样。
    /// </summary>
    /// <param name="seconds">间隔秒数（下限已由启动参数校验保证）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SetIntervalAsync(int seconds, CancellationToken cancellationToken = default)
    {
        var now = Now();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.SchedulerStates
            .Where(state => state.Id == 1)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(state => state.IntervalSeconds, seconds)
                    .SetProperty(state => state.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>写一条审计记录。</summary>
    /// <param name="actor">操作者（如 mcp:claude）。</param>
    /// <param name="action">动作名。</param>
    /// <param name="subject">对象。</param>
    /// <param name="detail">细节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task AuditAsync(
        string actor,
        string action,
        string? subject,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Audits.Add(new AuditEntity
        {
            At = Now(),
            Actor = actor,
            Action = action,
            Subject = subject,
            Detail = detail,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>当前 UTC 时间（ISO-8601）。</summary>
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>解析计数 JSON；损坏时返回空字典而不是抛错（观测数据不应阻断调度）。</summary>
    private static IReadOnlyDictionary<string, long> ParseCounters(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? new Dictionary<string, long>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, long>();
        }
    }
}
