using DevHubd.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Results;

/// <summary><c>notice</c> 一行的投影。</summary>
/// <param name="Seq">自增序号，单调递增，作为扩展轮询游标。</param>
/// <param name="Kind">result / failure / breaker / auth / provider。</param>
/// <param name="TaskId">关联任务 id；provider 级通知可能为空。</param>
/// <param name="Title">标题。</param>
/// <param name="Body">正文。</param>
/// <param name="CreatedAt">创建时间（ISO 8601）。</param>
public sealed record NoticeRecord(
    long Seq,
    string Kind,
    string? TaskId,
    string Title,
    string Body,
    string CreatedAt);

/// <summary>通知分页结果。</summary>
/// <param name="Items">序号大于游标且未确认的通知，按 seq 升序。</param>
/// <param name="LatestSeq">当前最大序号；扩展首次轮询据此建立基线，避免把历史积压当新工作。</param>
public sealed record NoticePage(IReadOnlyList<NoticeRecord> Items, long LatestSeq);

/// <summary>
/// 通知读取与确认。写入仍由 <c>ExecutionStore.InsertNoticeAsync</c> 负责。
/// 去重以数据库为准：<c>dedupe_key</c> 唯一约束保证同一状态进入只提醒一次；
/// <c>acked_at</c> 与单调 <c>seq</c> 保证崩溃重启后不会重复弹出同一条通知。
/// 读写统一通过 EF Core（<see cref="DevHubDbContext"/>）完成。
/// </summary>
public sealed class NoticeStore
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public NoticeStore(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>用裸连接工厂构造（测试与不依赖 DI 的场景）。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public NoticeStore(SqliteConnectionFactory factory)
        : this(new DevHubDbContextFactory(factory.ConnectionString))
    {
    }

    /// <summary>
    /// 列出序号大于 <paramref name="sinceSeq"/> 且未确认的通知。
    /// </summary>
    /// <param name="sinceSeq">上一轮已处理的最大序号；首次轮询传 0。</param>
    /// <param name="limit">最多返回条数（1–50）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentOutOfRangeException">limit 越界。</exception>
    public async Task<NoticePage> ListSinceAsync(
        long sinceSeq,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit 必须在 1–50 之间。");
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.Notices
            .AsNoTracking()
            .Where(notice => notice.Seq > sinceSeq && notice.AckedAt == null)
            .OrderBy(notice => notice.Seq)
            .Take(limit)
            .Select(notice => new
            {
                notice.Seq,
                notice.Kind,
                notice.TaskId,
                notice.Title,
                notice.Body,
                notice.CreatedAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(row => new NoticeRecord(
                row.Seq,
                row.Kind,
                row.TaskId,
                row.Title,
                row.Body ?? string.Empty,
                row.CreatedAt))
            .ToList();

        var latestSeq = await db.Notices
            .MaxAsync(notice => (long?)notice.Seq, cancellationToken)
            .ConfigureAwait(false) ?? 0;

        return new NoticePage(items, latestSeq);
    }

    /// <summary>把指定序号标记为已确认；已确认的不会重复提醒。</summary>
    /// <param name="seqs">通知序号列表。</param>
    /// <param name="now">时间戳（ISO 8601）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际更新的条数。</returns>
    public async Task<int> AckAsync(
        IReadOnlyList<long> seqs,
        string now,
        CancellationToken cancellationToken = default)
    {
        if (seqs.Count == 0)
        {
            return 0;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Notices
            .Where(notice => notice.AckedAt == null && seqs.Contains(notice.Seq))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(notice => notice.AckedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
