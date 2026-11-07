using DevHubd.Core.Data;

namespace DevHubd.Core.Providers;

/// <summary>
/// provider 状态存取：<c>provider_state</c> 以 provider 为主键，配额/鉴权失败只影响对应 provider，
/// 不会把其他 provider（例如 vscode 桥）一起拖下水（设计文档 §13.3）。
/// </summary>
public sealed class ProviderStateStore
{
    private readonly SqliteConnectionFactory _factory;

    /// <summary>注入连接工厂。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public ProviderStateStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>读取单个 provider 状态；不存在返回 null。</summary>
    /// <param name="provider">provider 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ProviderStateRecord?> GetAsync(string provider, CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider, state, detail, breaker, breaker_reason, breaker_opened_at,
                   consecutive_failures, checked_at
            FROM provider_state WHERE provider = $provider;
            """;
        command.Parameters.AddWithValue("$provider", provider);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>读取全部 provider 状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<ProviderStateRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider, state, detail, breaker, breaker_reason, breaker_opened_at,
                   consecutive_failures, checked_at
            FROM provider_state ORDER BY provider;
            """;

        var rows = new List<ProviderStateRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
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
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO provider_state
              (provider, state, detail, breaker, breaker_reason, breaker_opened_at, consecutive_failures, checked_at)
            VALUES
              ($provider, $state, $detail, $breaker, $reason, $openedAt, $failures, $checkedAt)
            ON CONFLICT (provider) DO UPDATE SET
              state = excluded.state,
              detail = excluded.detail,
              breaker = excluded.breaker,
              breaker_reason = excluded.breaker_reason,
              breaker_opened_at = excluded.breaker_opened_at,
              consecutive_failures = excluded.consecutive_failures,
              checked_at = excluded.checked_at;
            """;
        command.Parameters.AddWithValue("$provider", record.Provider);
        command.Parameters.AddWithValue("$state", record.State);
        command.Parameters.AddWithValue("$detail", (object?)record.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$breaker", record.Breaker);
        command.Parameters.AddWithValue("$reason", (object?)record.BreakerReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$openedAt", (object?)record.BreakerOpenedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$failures", record.ConsecutiveFailures);
        command.Parameters.AddWithValue("$checkedAt", record.CheckedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按列序读取状态行。</summary>
    private static ProviderStateRecord Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt32(6),
        reader.GetString(7));
}
