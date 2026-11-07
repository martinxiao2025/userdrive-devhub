using System.Text.Json;
using DevHubd.Core.Data;
using Microsoft.Data.Sqlite;

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
public sealed record ResultRecord(
    string Id,
    string TaskId,
    string QueueItemId,
    string Provider,
    string Outcome,
    string Summary,
    IReadOnlyList<string> Artifacts,
    string CreatedAt,
    string? ReadAt);

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
/// </summary>
public sealed class ResultStore
{
    private readonly SqliteConnectionFactory _factory;

    /// <summary>注入连接工厂。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public ResultStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
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

        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();

        var sql = new System.Text.StringBuilder("""
            SELECT id, task_id, queue_item_id, provider, outcome, summary, artifacts, created_at, read_at
            FROM result
            WHERE 1 = 1
            """);

        if (unreadOnly)
        {
            sql.Append(" AND read_at IS NULL");
        }

        if (TryDecodeCursor(cursor, out var cursorCreatedAt, out var cursorId))
        {
            sql.Append("""
                 AND (created_at < $cc OR (created_at = $cc AND id < $ci))
                """);
            command.Parameters.AddWithValue("$cc", cursorCreatedAt);
            command.Parameters.AddWithValue("$ci", cursorId);
        }

        sql.Append(" ORDER BY created_at DESC, id DESC LIMIT $limit;");
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$limit", limit);

        var items = new List<ResultRecord>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(Read(reader));
            }
        }

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

        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();

        var placeholders = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            placeholders[i] = $"$id{i}";
            command.Parameters.AddWithValue($"$id{i}", ids[i]);
        }

        command.CommandText = $"""
            UPDATE result
            SET read_at = $now
            WHERE read_at IS NULL AND id IN ({string.Join(", ", placeholders)});
            """;
        command.Parameters.AddWithValue("$now", now);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把所有未读结果标记为已读。</summary>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际被标记的条数。</returns>
    public async Task<int> AckAllAsync(string now, CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE result SET read_at = $now WHERE read_at IS NULL;";
        command.Parameters.AddWithValue("$now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>聚合未读计数：总数、失败数、待复核数。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ResultDigest> GetDigestAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) AS unread,
                   COALESCE(SUM(CASE WHEN outcome = 'failed' THEN 1 ELSE 0 END), 0) AS failures,
                   COALESCE(SUM(CASE WHEN outcome = 'awaiting_review' THEN 1 ELSE 0 END), 0) AS awaiting
            FROM result WHERE read_at IS NULL;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ResultDigest(0, 0, 0);
        }

        return new ResultDigest(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <summary>按列序读取一行结果。</summary>
    /// <param name="reader">数据读取器。</param>
    private static ResultRecord Read(SqliteDataReader reader)
    {
        var artifactsJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        var artifacts = artifactsJson is null
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(artifactsJson) ?? Array.Empty<string>();

        return new ResultRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            artifacts,
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
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
