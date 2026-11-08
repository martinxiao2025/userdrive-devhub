using DevHubd.Core.Data;
using DevHubd.Core.Models;
using DevHubd.Core.Scheduling;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Hosting;

/// <summary>
/// <see cref="ISchedulerStatusSource"/> 的宿主实现：读 <c>scheduler_state</c> 单例行、队列计数与 provider 状态。
/// 调度器由 <see cref="Scheduling.SchedulerBackgroundService"/> 驱动，这里只做只读聚合。
/// 所有聚合统一通过 EF Core（<see cref="DevHubDbContext"/>）完成，返回结构与 MCP 契约保持不变。
/// </summary>
public sealed class DevHubHostState : ISchedulerStatusSource
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;
    private readonly SchedulerStateStore _stateStore;
    private readonly HostInfo _host;

    /// <summary>注入 EF Core 上下文工厂、调度状态存取与宿主信息。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    /// <param name="stateStore">调度状态存取。</param>
    /// <param name="host">宿主信息。</param>
    public DevHubHostState(
        IDbContextFactory<DevHubDbContext> contextFactory,
        SchedulerStateStore stateStore,
        HostInfo host)
    {
        _contextFactory = contextFactory;
        _stateStore = stateStore;
        _host = host;
    }

    /// <summary>用裸连接工厂构造（不依赖 DI 的场景）；内部转换为 EF 上下文工厂。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    /// <param name="stateStore">调度状态存取。</param>
    /// <param name="host">宿主信息。</param>
    public DevHubHostState(SqliteConnectionFactory factory, SchedulerStateStore stateStore, HostInfo host)
        : this(new DevHubDbContextFactory(factory.ConnectionString), stateStore, host)
    {
    }

    /// <summary>
    /// 组装状态快照。数据库不可用时抛出异常，由工具层转换为 <c>SCHEDULER_UNAVAILABLE</c>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SchedulerStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var scheduler = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // schema 版本仍由 Migrator 负责读取（它是 DDL/版本唯一管理者），
        // 只是改为在 EF 已打开的连接上读取，不再单独开一条裸连接。
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var schemaVersion = await Migrator
            .GetUserVersionAsync((SqliteConnection)db.Database.GetDbConnection(), cancellationToken)
            .ConfigureAwait(false);

        var queue = await ReadQueueCountersAsync(db, cancellationToken).ConfigureAwait(false);
        var unread = await ReadUnreadAsync(db, cancellationToken).ConfigureAwait(false);
        var providers = await ReadProvidersAsync(db, cancellationToken).ConfigureAwait(false);
        var tokens = await ReadTokenCountersAsync(db, cancellationToken).ConfigureAwait(false);

        return new SchedulerStatusSnapshot
        {
            Enabled = scheduler.Enabled,
            Mode = scheduler.Mode,
            Phase = scheduler.Enabled ? "idle" : "paused",
            PauseReason = scheduler.PauseReason,
            IntervalSeconds = scheduler.IntervalSeconds,
            LastScanAt = UtcTime.Normalize(scheduler.LastScanAt),
            NextScanAt = UtcTime.Normalize(scheduler.NextScanAt),
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
                StartedAt = UtcTime.Format(_host.StartedAt),
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
    /// <param name="db">上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<QueueCounters> ReadQueueCountersAsync(DevHubDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.QueueItems
            .AsNoTracking()
            .GroupBy(item => item.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var counters = new QueueCounters();
        foreach (var row in rows)
        {
            counters = row.Status switch
            {
                "pending" => counters with { Pending = row.Count },
                "claimed" => counters with { Claimed = row.Count },
                "running" => counters with { Running = row.Count },
                "dead" => counters with { Dead = row.Count },
                _ => counters,
            };
        }

        return counters;
    }

    /// <summary>统计未读结果与未读失败数。</summary>
    /// <param name="db">上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<UnreadCounters> ReadUnreadAsync(DevHubDbContext db, CancellationToken cancellationToken)
    {
        var total = await db.Results
            .AsNoTracking()
            .CountAsync(result => result.ReadAt == null, cancellationToken)
            .ConfigureAwait(false);

        var failures = await db.Results
            .AsNoTracking()
            .CountAsync(result => result.ReadAt == null && result.Outcome == "failed", cancellationToken)
            .ConfigureAwait(false);

        return new UnreadCounters
        {
            Results = total,
            Failures = failures,
        };
    }

    /// <summary>
    /// 汇总全部 attempt 的 token 用量。没有 token 的 attempt（provider 未返回 usage，
    /// 或尚未真正调用模型就收尾）单列计数，避免把缺失当成 0 混入总量。
    /// </summary>
    /// <param name="db">上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<TokenCounters> ReadTokenCountersAsync(
        DevHubDbContext db,
        CancellationToken cancellationToken)
    {
        // 单条聚合查询：SUM 对空表返回 NULL，用可空类型承接后再归零，语义与 COALESCE(SUM(...),0) 一致。
        var aggregate = await db.Attempts
            .AsNoTracking()
            .GroupBy(attempt => 1)
            .Select(group => new
            {
                Prompt = group.Sum(attempt => attempt.InputTokens),
                Completion = group.Sum(attempt => attempt.OutputTokens),
                WithUsage = group.Count(attempt => attempt.InputTokens != null || attempt.OutputTokens != null),
                WithoutUsage = group.Count(attempt => attempt.InputTokens == null && attempt.OutputTokens == null),
                Estimated = group.Count(attempt => attempt.UsageEstimated),
            })
            // GroupBy(1) 至多一行，用 SingleOrDefault 表达“聚合结果唯一”，也避免 EF 对无排序 First 的警告。
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (aggregate is null)
        {
            return new TokenCounters();
        }

        var prompt = aggregate.Prompt ?? 0;
        var completion = aggregate.Completion ?? 0;

        return new TokenCounters
        {
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = prompt + completion,
            AttemptsWithUsage = aggregate.WithUsage,
            AttemptsWithoutUsage = aggregate.WithoutUsage,
            EstimatedAttempts = aggregate.Estimated,
        };
    }

    /// <summary>读取 provider 状态；表为空时给出未配置的默认两项。</summary>
    /// <param name="db">上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<IReadOnlyList<ProviderStatus>> ReadProvidersAsync(
        DevHubDbContext db,
        CancellationToken cancellationToken)
    {
        var rows = await db.ProviderStates
            .AsNoTracking()
            .OrderBy(state => state.Provider)
            .Select(state => new
            {
                state.Provider,
                state.State,
                state.Breaker,
                state.Detail,
                state.CheckedAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var list = rows
            .Select(state => new ProviderStatus
            {
                Provider = state.Provider,
                State = state.State,
                Breaker = state.Breaker,
                Detail = state.Detail,
                CheckedAt = UtcTime.Normalize(state.CheckedAt),
            })
            .ToList();

        if (list.Count == 0)
        {
            var checkedAt = UtcTime.Now();
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
