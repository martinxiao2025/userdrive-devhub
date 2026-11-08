using DevHubd.Core.Data;
using DevHubd.Core.Scheduling;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using Microsoft.Data.Sqlite;

namespace DevHubd.Hosting;

/// <summary>
/// <see cref="ISchedulerStatusSource"/> 的宿主实现：读 <c>scheduler_state</c> 单例行、队列计数与 provider 状态。
/// 调度器由 <see cref="Scheduling.SchedulerBackgroundService"/> 驱动，这里只做只读聚合。
/// </summary>
public sealed class DevHubHostState : ISchedulerStatusSource
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SchedulerStateStore _stateStore;
    private readonly HostInfo _host;

    /// <summary>注入连接工厂、调度状态存取与宿主信息。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    /// <param name="stateStore">调度状态存取。</param>
    /// <param name="host">宿主信息。</param>
    public DevHubHostState(SqliteConnectionFactory factory, SchedulerStateStore stateStore, HostInfo host)
    {
        _factory = factory;
        _stateStore = stateStore;
        _host = host;
    }

    /// <summary>
    /// 组装状态快照。数据库不可用时抛出异常，由工具层转换为 <c>SCHEDULER_UNAVAILABLE</c>。
    /// </summary>
    /// <param name="verbose">是否要求更详细的信息（当前不影响返回内容）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerStatusSnapshot> GetSnapshotAsync(bool verbose, CancellationToken cancellationToken = default)
    {
        var scheduler = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);

        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var schemaVersion = await Migrator.GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var queue = await ReadQueueCountersAsync(connection, cancellationToken).ConfigureAwait(false);
        var unread = await ReadUnreadAsync(connection, cancellationToken).ConfigureAwait(false);
        var providers = await ReadProvidersAsync(connection, cancellationToken).ConfigureAwait(false);
        var tokens = await ReadTokenCountersAsync(connection, cancellationToken).ConfigureAwait(false);

        return new SchedulerStatusSnapshot
        {
            Enabled = scheduler.Enabled,
            Mode = scheduler.Mode,
            Phase = scheduler.Enabled ? "idle" : "paused",
            PauseReason = scheduler.PauseReason,
            IntervalSeconds = scheduler.IntervalSeconds,
            LastScanAt = scheduler.LastScanAt,
            NextScanAt = scheduler.NextScanAt,
            Counters = MapCounters(scheduler.Counters),
            Queue = queue,
            Unread = unread,
            Tokens = tokens,
            Providers = providers,
            Mcp = new McpBindingStatus { Bound = true, Port = _host.Options.McpPort },
            Host = new HostStatus
            {
                Pid = Environment.ProcessId,
                EngineMode = "mcp+scheduler",
                StartedAt = _host.StartedAt.ToString("O"),
                UptimeSeconds = _host.UptimeSeconds,
                Version = _host.Version,
                DataDirectory = _host.Paths.DataDirectory,
                SchemaVersion = schemaVersion,
            },
        };
    }

    /// <summary>把库中的累计计数映射为状态契约。</summary>
    /// <param name="counters">库中的计数键值。</param>
    private static SchedulerCounters MapCounters(IReadOnlyDictionary<string, long> counters) => new()
    {
        Scanned = (int)counters.GetValueOrDefault("scanned"),
        Enqueued = (int)counters.GetValueOrDefault("enqueued"),
        Reclaimed = (int)counters.GetValueOrDefault("reclaimed"),
    };

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

    /// <summary>
    /// 汇总全部 attempt 的 token 用量。没有 token 的 attempt（provider 未返回 usage，
    /// 或尚未真正调用模型就收尾）单列计数，避免把缺失当成 0 混入总量。
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<TokenCounters> ReadTokenCountersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(input_tokens), 0) AS prompt,
                   COALESCE(SUM(output_tokens), 0) AS completion,
                   COUNT(CASE WHEN input_tokens IS NOT NULL OR output_tokens IS NOT NULL THEN 1 END) AS with_usage,
                   COUNT(CASE WHEN input_tokens IS NULL AND output_tokens IS NULL THEN 1 END) AS without_usage,
                   COALESCE(SUM(usage_estimated), 0) AS estimated
            FROM attempt;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new TokenCounters();
        }

        var prompt = reader.GetInt32(0);
        var completion = reader.GetInt32(1);
        return new TokenCounters
        {
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = prompt + completion,
            AttemptsWithUsage = reader.GetInt32(2),
            AttemptsWithoutUsage = reader.GetInt32(3),
            EstimatedAttempts = reader.GetInt32(4),
        };
    }

    /// <summary>读取 provider 状态；表为空时给出未配置的默认两项。</summary>
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
                Detail = "未配置 BYOK provider",
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
}
