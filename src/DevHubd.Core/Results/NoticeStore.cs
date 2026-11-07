using DevHubd.Core.Data;
using Microsoft.Data.Sqlite;

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
/// </summary>
public sealed class NoticeStore
{
    private readonly SqliteConnectionFactory _factory;

    /// <summary>注入连接工厂。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public NoticeStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
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

        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var items = new List<NoticeRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT seq, kind, task_id, title, COALESCE(body, ''), created_at
                FROM notice
                WHERE seq > $since AND acked_at IS NULL
                ORDER BY seq ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$since", sinceSeq);
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new NoticeRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            }
        }

        long latestSeq;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM notice;";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            latestSeq = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

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

        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();

        var placeholders = new string[seqs.Count];
        for (var i = 0; i < seqs.Count; i++)
        {
            placeholders[i] = $"$seq{i}";
            command.Parameters.AddWithValue($"$seq{i}", seqs[i]);
        }

        command.CommandText = $"""
            UPDATE notice
            SET acked_at = $now
            WHERE acked_at IS NULL AND seq IN ({string.Join(", ", placeholders)});
            """;
        command.Parameters.AddWithValue("$now", now);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
