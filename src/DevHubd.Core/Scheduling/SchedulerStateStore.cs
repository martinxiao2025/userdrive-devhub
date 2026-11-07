using System.Text.Json;
using DevHubd.Core.Data;
using Microsoft.Data.Sqlite;

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
/// </summary>
public sealed class SchedulerStateStore
{
    private readonly SqliteConnectionFactory _factory;

    /// <summary>注入连接工厂。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public SchedulerStateStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>读取当前状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerState> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT enabled, mode, pause_reason, interval_seconds, last_scan_at, next_scan_at, counters
            FROM scheduler_state WHERE id = 1;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("scheduler_state 缺少 id=1 的行，迁移可能未完成。");
        }

        return new SchedulerState(
            reader.GetInt64(0) == 1,
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            ParseCounters(reader.IsDBNull(6) ? "{}" : reader.GetString(6)));
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

        await UpdateAsync(
            """
            UPDATE scheduler_state
            SET last_scan_at = $last, next_scan_at = $next, counters = $counters, updated_at = $now
            WHERE id = 1;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$last", lastScanAt);
                command.Parameters.AddWithValue("$next", nextScanAt);
                command.Parameters.AddWithValue("$counters", JsonSerializer.Serialize(counters));
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>启用或暂停调度。</summary>
    /// <param name="enabled">目标状态。</param>
    /// <param name="reason">暂停原因；恢复时传 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SetEnabledAsync(bool enabled, string? reason, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(
            """
            UPDATE scheduler_state
            SET enabled = $enabled, pause_reason = $reason, updated_at = $now
            WHERE id = 1;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            },
            cancellationToken).ConfigureAwait(false);
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

        await UpdateAsync(
            """
            UPDATE scheduler_state
            SET mode = $mode, updated_at = $now
            WHERE id = 1;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$mode", mode);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            },
            cancellationToken).ConfigureAwait(false);
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
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit (at, actor, action, subject, detail)
            VALUES ($at, $actor, $action, $subject, $detail);
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>执行一条更新语句。</summary>
    private async Task UpdateAsync(string sql, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

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
