using Devhubd.Core.Data;
using Devhubd.Mcp.Abstractions;
using Devhubd.Mcp.Contracts;
using Microsoft.Data.Sqlite;

namespace Devhubd.Hosting;

/// <summary>
/// <see cref="ISchedulerStatusSource"/> 的宿主实现：读 SQLite 中的单例状态行、队列计数与 provider 状态。
/// P0 不做任何写操作，也不启动定时器。
/// </summary>
public sealed class DevhubHostState : ISchedulerStatusSource
{
    private readonly SqliteConnectionFactory _factory;
    private readonly HostInfo _host;

    /// <summary>注入连接工厂与宿主信息。</summary>
    public DevhubHostState(SqliteConnectionFactory factory, HostInfo host)
    {
        _factory = factory;
        _host = host;
    }

    /// <summary>
    /// 组装状态快照。数据库不可用时抛出异常，由工具层转换为 <c>SCHEDULER_UNAVAILABLE</c>。
    /// </summary>
    public async Task<SchedulerStatusSnapshot> GetSnapshotAsync(bool verbose, CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var schemaVersion = await Migrator.GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var scheduler = await ReadSchedulerStateAsync(connection, cancellationToken).ConfigureAwait(false);
        var queue = await ReadQueueCountersAsync(connection, cancellationToken).ConfigureAwait(false);
        var unread = await ReadUnreadAsync(connection, cancellationToken).ConfigureAwait(false);
        var providers = await ReadProvidersAsync(connection, cancellationToken).ConfigureAwait(false);

        return new SchedulerStatusSnapshot
        {
            Enabled = scheduler.Enabled,
            Mode = scheduler.Mode,
            Phase = scheduler.PauseReason is null ? "idle" : "paused",
            PauseReason = scheduler.PauseReason,
            IntervalSeconds = scheduler.IntervalSeconds,
            LastScanAt = scheduler.LastScanAt,
            NextScanAt = scheduler.NextScanAt,
            Queue = queue,
            Unread = unread,
            Providers = providers,
            Mcp = new McpBindingStatus { Bound = true, Port = _host.Options.McpPort },
            Host = new HostStatus
            {
                Pid = Environment.ProcessId,
                EngineMode = "skeleton",
                StartedAt = _host.StartedAt.ToString("O"),
                UptimeSeconds = _host.UptimeSeconds,
                Version = _host.Version,
                DataDirectory = _host.Paths.DataDirectory,
                SchemaVersion = schemaVersion,
            },
        };
    }

    /// <summary>读取单例调度状态行；缺失时回落到 P0 默认值。</summary>
    private async Task<SchedulerRow> ReadSchedulerStateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT enabled, mode, pause_reason, interval_seconds, last_scan_at, next_scan_at " +
            "FROM scheduler_state WHERE id = 1;";

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SchedulerRow(true, _host.Options.DeliveryMode, null, _host.Options.IntervalSeconds, null, null);
        }

        return new SchedulerRow(
            reader.GetInt64(0) != 0,
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    /// <summary>按状态聚合队列项数量。</summary>
    private static async Task<QueueCounters> ReadQueueCountersAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, COUNT(*) FROM queue_item GROUP BY status;";

        var counters = new QueueCounters();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var count = reader.GetInt32(1);
            counters = reader.GetString(0) switch
            {
                "pending" => counters with { Pending = count },
                "claimed" => counters with { Claimed = count },
                "running" => counters with { Running = count },
                "dead" => counters with { Dead = count },
                _ => counters,
            };
        }

        return counters;
    }

    /// <summary>统计未读结果与未读失败数。</summary>
    private static async Task<UnreadCounters> ReadUnreadAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) AS total, " +
            "SUM(CASE WHEN outcome = 'failed' THEN 1 ELSE 0 END) AS failures " +
            "FROM result WHERE read_at IS NULL;";

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new UnreadCounters();
        }

        return new UnreadCounters
        {
            Results = reader.GetInt32(0),
            Failures = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
        };
    }

    /// <summary>读取 provider 状态；表为空时给出"骨架未配置"的默认两项。</summary>
    private static async Task<IReadOnlyList<ProviderStatus>> ReadProvidersAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider, state, breaker, detail, checked_at FROM provider_state ORDER BY provider;";

        var list = new List<ProviderStatus>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new ProviderStatus
            {
                Provider = reader.GetString(0),
                State = reader.GetString(1),
                Breaker = reader.GetString(2),
                Detail = reader.IsDBNull(3) ? null : reader.GetString(3),
                CheckedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        if (list.Count == 0)
        {
            var checkedAt = DateTimeOffset.UtcNow.ToString("O");
            list.Add(new ProviderStatus
            {
                Provider = "openai",
                State = "down",
                Breaker = "closed",
                Detail = "P0 骨架：未配置 BYOK provider",
                CheckedAt = checkedAt,
            });
            list.Add(new ProviderStatus
            {
                Provider = "vscode",
                State = "down",
                Breaker = "closed",
                Detail = "扩展未激活或未安装",
                CheckedAt = checkedAt,
            });
        }

        return list;
    }

    /// <summary>scheduler_state 行的投影。</summary>
    private sealed record SchedulerRow(
        bool Enabled,
        string Mode,
        string? PauseReason,
        int IntervalSeconds,
        string? LastScanAt,
        string? NextScanAt);
}
