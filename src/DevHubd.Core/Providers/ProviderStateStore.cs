using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Providers;

/// <summary>
/// provider 状态存取：<c>provider_state</c> 以 provider 为主键，配额/鉴权失败只影响对应 provider，
/// 不会把其他 provider（例如 vscode 桥）一起拖下水（设计文档 §13.3）。
/// 读取通过 EF Core（<see cref="DevHubDbContext"/>）完成；写入使用 SQLite 原生
/// <c>INSERT … ON CONFLICT DO UPDATE</c>（EF 没有等价写法），保持单语句幂等覆盖。
/// </summary>
public sealed class ProviderStateStore
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public ProviderStateStore(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>用裸连接工厂构造（测试与不依赖 DI 的场景）。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public ProviderStateStore(SqliteConnectionFactory factory)
        : this(new DevHubDbContextFactory(factory.ConnectionString))
    {
    }

    /// <summary>读取单个 provider 状态；不存在返回 null。</summary>
    /// <param name="provider">provider 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ProviderStateRecord?> GetAsync(string provider, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ProviderStates
            .AsNoTracking()
            .Where(state => state.Provider == provider)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToDomain(row);
    }

    /// <summary>读取全部 provider 状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<ProviderStateRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ProviderStates
            .AsNoTracking()
            .OrderBy(state => state.Provider)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDomain).ToList();
    }

    /// <summary>
    /// 判断是否允许派发：熔断 open 且冷却未到 → false；冷却到期 → 落库为 half_open 并放行一次。
    /// </summary>
    /// <param name="provider">provider 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> IsDispatchAllowedAsync(string provider, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(provider, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        if (!BreakerPolicy.IsDispatchAllowed(current, now, out var effective))
        {
            return false;
        }

        if (current is not null && !string.Equals(current.Breaker, effective, StringComparison.Ordinal))
        {
            await SaveAsync(current with { Breaker = effective, CheckedAt = now.ToString("O") }, cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>记录一次成功：status=ready，熔断关闭，连续失败清零。</summary>
    /// <param name="provider">provider 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RecordSuccessAsync(string provider, CancellationToken cancellationToken = default)
    {
        var record = BreakerPolicy.OnSuccess(provider, DateTimeOffset.UtcNow);
        await SaveAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>记录一次失败：按错误分类决定立即熔断、连续失败累计或仅置 down。</summary>
    /// <param name="provider">provider 标识。</param>
    /// <param name="kind">错误分类。</param>
    /// <param name="detail">说明；不得包含凭据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RecordFailureAsync(
        string provider,
        string kind,
        string detail,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(provider, cancellationToken).ConfigureAwait(false);
        var updated = BreakerPolicy.OnFailure(current, kind, detail, DateTimeOffset.UtcNow) with
        {
            Provider = provider,
        };

        await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 人工重置熔断：仅当熔断处于 <c>open</c> / <c>half_open</c> 时生效，
    /// 关闭熔断并清零连续失败；provider 可用状态保持不变（仍由下一次探测决定）。
    /// </summary>
    /// <param name="provider">provider 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否确实重置了处于打开状态的熔断。</returns>
    public async Task<bool> ResetBreakerAsync(string provider, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(provider, cancellationToken).ConfigureAwait(false);
        if (current is null || string.Equals(current.Breaker, BreakerStates.Closed, StringComparison.Ordinal))
        {
            return false;
        }

        await SaveAsync(
            current with
            {
                Breaker = BreakerStates.Closed,
                BreakerReason = null,
                BreakerOpenedAt = null,
                ConsecutiveFailures = 0,
                CheckedAt = DateTimeOffset.UtcNow.ToString("O"),
            },
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>写入一条状态（覆盖式）。</summary>
    /// <param name="record">状态行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SaveAsync(ProviderStateRecord record, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // 覆盖式写入必须原子：EF 没有 UPSERT，先查后插在并发探测下会撞唯一约束，
        // 因此保留这一条 SQLite 原生语句（语义与改造前完全一致）。
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO provider_state
              (provider, state, detail, breaker, breaker_reason, breaker_opened_at, consecutive_failures, checked_at)
            VALUES
              ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})
            ON CONFLICT (provider) DO UPDATE SET
              state = excluded.state,
              detail = excluded.detail,
              breaker = excluded.breaker,
              breaker_reason = excluded.breaker_reason,
              breaker_opened_at = excluded.breaker_opened_at,
              consecutive_failures = excluded.consecutive_failures,
              checked_at = excluded.checked_at;
            """,
            new object[]
            {
                record.Provider,
                record.State,
                // null 值直接以 null 传递：EF 会为无类型参数生成 NULL，不需要（也不应）传 DBNull。
                record.Detail!,
                record.Breaker,
                record.BreakerReason!,
                record.BreakerOpenedAt!,
                record.ConsecutiveFailures,
                record.CheckedAt,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把持久化模型映射为对外投影。</summary>
    /// <param name="entity">持久化模型。</param>
    private static ProviderStateRecord ToDomain(ProviderStateEntity entity) => new(
        entity.Provider,
        entity.State,
        entity.Detail,
        entity.Breaker,
        entity.BreakerReason,
        entity.BreakerOpenedAt,
        entity.ConsecutiveFailures,
        entity.CheckedAt);
}
