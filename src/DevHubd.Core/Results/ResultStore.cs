using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Results;

/// <summary><c>result</c> 一行的投影。</summary>
/// <param name="Id">结果 id。</param>
/// <param name="TaskId">任务 id。</param>
/// <param name="QueueItemId">队列项 id。</param>
/// <param name="Provider">产出该结果的 provider。</param>
/// <param name="Outcome">delivered / awaiting_review / failed。</param>
/// <param name="Summary">摘要。</param>
/// <param name="Artifacts">产物相对路径。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="ReadAt">已读时间；null 表示未读。</param>
/// <param name="ToolCalls">最近一次 attempt 的工具调用次数；未记录时为 null。</param>
/// <param name="InputTokens">最近一次 attempt 的输入 token；未获得 usage 时为 null。</param>
/// <param name="OutputTokens">最近一次 attempt 的输出 token；未获得 usage 时为 null。</param>
/// <param name="UsageEstimated">true 表示 token 为估算值，不是 provider 返回的真实 token。</param>
public sealed record ResultRecord(
    string Id,
    string TaskId,
    string QueueItemId,
    string Provider,
    string Outcome,
    string Summary,
    IReadOnlyList<string> Artifacts,
    string CreatedAt,
    string? ReadAt,
    int? ToolCalls,
    int? InputTokens,
    int? OutputTokens,
    bool UsageEstimated);

/// <summary>结果分页。</summary>
/// <param name="Items">当前页。</param>
/// <param name="NextCursor">下一页游标；null 表示到底。</param>
public sealed record ResultPage(IReadOnlyList<ResultRecord> Items, string? NextCursor);

/// <summary>未读结果的聚合计数（摘要与状态快照共用）。</summary>
/// <param name="Unread">未读总数。</param>
/// <param name="Failures">未读失败数。</param>
/// <param name="AwaitingReview">未读待复核数。</param>
public sealed record ResultDigest(int Unread, int Failures, int AwaitingReview);

/// <summary>
/// 结果存取：分页查询、标记已读与未读聚合。
/// 写入仍由 <c>ExecutionStore</c> 负责，本类只做"给 Chat 看"的读侧与 ack。
/// 读写统一通过 EF Core（<see cref="DevHubDbContext"/>）完成。
/// </summary>
public sealed class ResultStore
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public ResultStore(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>用裸连接工厂构造（测试与不依赖 DI 的场景）。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public ResultStore(SqliteConnectionFactory factory)
        : this(new DevHubDbContextFactory(factory.ConnectionString))
    {
    }

    /// <summary>
    /// 分页列出结果。顺序固定为 <c>created_at DESC, id DESC</c>，游标基于该顺序的键集分页。
    /// </summary>
    /// <param name="unreadOnly">true 时只返回未读。</param>
    /// <param name="limit">1–100。</param>
    /// <param name="cursor">上一页返回的 nextCursor。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentOutOfRangeException">limit 越界。</exception>
    public async Task<ResultPage> ListAsync(
        bool unreadOnly,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit 必须在 1–100 之间。");
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Results.AsNoTracking();

        if (unreadOnly)
        {
            query = query.Where(result => result.ReadAt == null);
        }

        if (TryDecodeCursor(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(result =>
                result.CreatedAt.CompareTo(cursorCreatedAt) < 0 ||
                (result.CreatedAt == cursorCreatedAt && result.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(result => result.CreatedAt)
            .ThenByDescending(result => result.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 最近一次 attempt（attempt_no 最大）用一次批量查询补齐，避免相关子查询在 EF 里被翻译成低效计划。
        var queueItemIds = rows
            .Select(result => result.QueueItemId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var attempts = queueItemIds.Count == 0
            ? new List<AttemptEntity>()
            : await db.Attempts
                .AsNoTracking()
                .Where(attempt => queueItemIds.Contains(attempt.QueueItemId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var latestAttempts = new Dictionary<string, AttemptEntity>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            if (!latestAttempts.TryGetValue(attempt.QueueItemId, out var current) || attempt.AttemptNo > current.AttemptNo)
            {
                latestAttempts[attempt.QueueItemId] = attempt;
            }
        }

        var items = rows
            .Select(result => Map(result, latestAttempts.GetValueOrDefault(result.QueueItemId)))
            .ToList();

        var nextCursor = items.Count == limit
            ? EncodeCursor(items[^1].CreatedAt, items[^1].Id)
            : null;

        return new ResultPage(items, nextCursor);
    }

    /// <summary>把指定结果标记为已读；已读的不会重复计数。</summary>
    /// <param name="ids">结果 id 列表。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际被标记的条数。</returns>
    public async Task<int> AckAsync(IReadOnlyList<string> ids, string now, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Results
            .Where(result => result.ReadAt == null && ids.Contains(result.Id))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(result => result.ReadAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>把所有未读结果标记为已读。</summary>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际被标记的条数。</returns>
    public async Task<int> AckAllAsync(string now, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Results
            .Where(result => result.ReadAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(result => result.ReadAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>聚合未读计数：总数、失败数、待复核数。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ResultDigest> GetDigestAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var digest = await db.Results
            .AsNoTracking()
            .Where(result => result.ReadAt == null)
            .GroupBy(result => 1)
            .Select(group => new
            {
                Unread = group.Count(),
                Failures = group.Count(result => result.Outcome == "failed"),
                AwaitingReview = group.Count(result => result.Outcome == "awaiting_review"),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return digest is null
            ? new ResultDigest(0, 0, 0)
            : new ResultDigest(digest.Unread, digest.Failures, digest.AwaitingReview);
    }

    /// <summary>把持久化模型映射为结果投影（附带最近一次 attempt 的用量字段）。</summary>
    /// <param name="result">结果行。</param>
    /// <param name="attempt">最近一次 attempt；没有则为 null。</param>
    private static ResultRecord Map(ResultEntity result, AttemptEntity? attempt)
    {
        var artifacts = result.Artifacts is null
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(result.Artifacts) ?? Array.Empty<string>();

        return new ResultRecord(
            result.Id,
            result.TaskId,
            result.QueueItemId,
            result.Provider,
            result.Outcome,
            result.Summary,
            artifacts,
            result.CreatedAt,
            result.ReadAt,
            attempt?.ToolCalls,
            attempt?.InputTokens,
            attempt?.OutputTokens,
            attempt?.UsageEstimated ?? false);
    }

    /// <summary>生成下一页游标（base64url 编码的 createdAt/id）。</summary>
    /// <param name="createdAt">最后一条的创建时间。</param>
    /// <param name="id">最后一条的 id。</param>
    private static string EncodeCursor(string createdAt, string id)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CursorPayload(createdAt, id));
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>解析游标；非法游标视为"从头开始"。</summary>
    /// <param name="cursor">游标。</param>
    /// <param name="createdAt">解析出的创建时间。</param>
    /// <param name="id">解析出的 id。</param>
    private static bool TryDecodeCursor(string? cursor, out string createdAt, out string id)
    {
        createdAt = string.Empty;
        id = string.Empty;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var payload = JsonSerializer.Deserialize<CursorPayload>(Convert.FromBase64String(padded));
            if (payload is null)
            {
                return false;
            }

            createdAt = payload.CreatedAt;
            id = payload.Id;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>游标承载的键集字段。</summary>
    private sealed record CursorPayload(string CreatedAt, string Id);
}
