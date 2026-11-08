using System.Globalization;
using System.Text;
using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Execution;
using DevHubd.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace DevHubd.Core.Queue;

/// <summary>
/// 队列服务：入队（按 dedupe_key 幂等）、原子认领、租约续期与回收、分页查询。
/// 并发安全依赖 SQLite 的 <c>BEGIN IMMEDIATE</c>（写锁前置）+ <c>busy_timeout</c>，
/// 而不是进程内 <c>lock</c>——后者在多连接下不成立。
/// </summary>
public sealed class QueueService
{
    private static readonly string[] _allowedStatuses =
    {
        "pending", "claimed", "running", "succeeded", "failed", "dead", "cancelled",
    };

    private readonly SqliteConnectionFactory _factory;

    /// <summary>注入连接工厂。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public QueueService(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>读取单个任务（task_view 投影）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<TaskRow?> GetTaskAsync(string workspaceId, string taskId, CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, title, status, content_hash, priority FROM task_view WHERE workspace_id = $workspace AND id = $task;";
        command.Parameters.AddWithValue("$workspace", workspaceId);
        command.Parameters.AddWithValue("$task", taskId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TaskRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4));
    }

    /// <summary>把工作区内所有 ready 任务翻译成队列项；重复调用零副作用。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="provider">定时派发的 provider；非法值回退 <c>openai</c>（由 <see cref="ExecutionOptions.NormalizeScheduledProvider"/> 收敛）。</param>
    /// <param name="dryRun">只统计不写入。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<EnqueueResult> EnqueueReadyTasksAsync(
        string workspaceId,
        string provider,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var now = Now();
        var scheduledProvider = ExecutionOptions.NormalizeScheduledProvider(provider);
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        var tasks = new List<TaskRow>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT id, title, status, content_hash, priority FROM task_view " +
                "WHERE workspace_id = $workspace AND status = 'ready' ORDER BY priority DESC, id ASC;";
            select.Parameters.AddWithValue("$workspace", workspaceId);

            using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tasks.Add(new TaskRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
            }
        }

        var enqueued = 0;
        var deduped = 0;
        var skippedActive = 0;

        foreach (var task in tasks)
        {
            var dedupeKey = BuildDedupeKey(workspaceId, task.Id, task.ContentHash);

            if (await FindActiveItemAsync(connection, transaction, workspaceId, task.Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                skippedActive++;
                continue;
            }

            if (dryRun)
            {
                enqueued++;
                continue;
            }

            var inserted = await InsertAsync(
                connection,
                transaction,
                workspaceId,
                task,
                scheduledProvider,
                dedupeKey,
                now,
                cancellationToken).ConfigureAwait(false);

            if (inserted)
            {
                enqueued++;
            }
            else
            {
                deduped++;
            }
        }

        if (!dryRun)
        {
            transaction.Commit();
        }

        return new EnqueueResult(enqueued, deduped, skippedActive);
    }

    /// <summary>手动把单个任务入队（<c>dispatch_now</c> 使用，不涉及模型调用）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="task">任务投影。</param>
    /// <param name="provider">首选 provider。</param>
    /// <param name="dryRun">只判断是否可入队，不写库。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ManualEnqueueResult> EnqueueTaskAsync(
        string workspaceId,
        TaskRow task,
        string provider,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var now = Now();
        var dedupeKey = BuildDedupeKey(workspaceId, task.Id, task.ContentHash);

        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        var existing = await FindByDedupeKeyAsync(connection, transaction, dedupeKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // 返回已有队列项的真实 provider，而不是本次请求的 provider——否则 deduped 会误导调用方。
            return new ManualEnqueueResult(false, true, existing.Id, "deduped", existing.Provider);
        }

        var active = await FindActiveItemAsync(connection, transaction, workspaceId, task.Id, cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            return new ManualEnqueueResult(false, true, active.Id, "active_exists", active.Provider);
        }

        if (dryRun)
        {
            return new ManualEnqueueResult(true, false, null, "would_enqueue", provider);
        }

        var id = Guid.NewGuid().ToString("N");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO queue_item
                  (id, workspace_id, task_id, task_hash, dedupe_key, status, priority, attempt, max_attempts,
                   preferred_provider, available_at, created_at, updated_at)
                VALUES
                  ($id, $workspace, $task, $hash, $dedupe, 'pending', $priority, 0, 3, $provider, $now, $now, $now);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$workspace", workspaceId);
            insert.Parameters.AddWithValue("$task", task.Id);
            insert.Parameters.AddWithValue("$hash", task.ContentHash);
            insert.Parameters.AddWithValue("$dedupe", dedupeKey);
            insert.Parameters.AddWithValue("$priority", task.Priority);
            insert.Parameters.AddWithValue("$provider", provider);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return new ManualEnqueueResult(true, false, id, "pending", provider);
    }

    /// <summary>读取任务完整文档（从 <c>task_view.payload</c> 还原并复用同一套校验）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<TaskDocument?> GetTaskDocumentAsync(
        string workspaceId,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM task_view WHERE workspace_id = $workspace AND id = $task;";
        command.Parameters.AddWithValue("$workspace", workspaceId);
        command.Parameters.AddWithValue("$task", taskId);

        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (payload is null)
        {
            return null;
        }

        return TaskFileReader.TryParse(payload, out var document, out _) ? document : null;
    }

    /// <summary>
    /// 原子认领一个可执行队列项：单条 <c>UPDATE … RETURNING</c>，在 IMMEDIATE 事务内完成，
    /// 因此并发调用只可能有一个成功。认领即写入 attempt 行并递增 attempt。
    /// </summary>
    /// <param name="ownerId">认领者标识。</param>
    /// <param name="lease">租约时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<QueueItem?> ClaimAsync(string ownerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var claimed = await ClaimDetailedAsync(ownerId, lease, cancellationToken).ConfigureAwait(false);
        return claimed?.Item;
    }

    /// <summary>
    /// 原子认领并返回 attempt id，供执行器回写 attempt 终态。
    /// 认领条件包含 <c>attempt &lt; max_attempts</c>，避免用尽预算的项被反复认领。
    /// </summary>
    /// <param name="ownerId">认领者标识。</param>
    /// <param name="lease">租约时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ClaimedQueueItem?> ClaimDetailedAsync(
        string ownerId,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var nowValue = DateTimeOffset.UtcNow;
        var now = nowValue.ToString("O");
        var expires = nowValue.Add(lease).ToString("O");

        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        QueueItem? item = null;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE queue_item
                SET status = 'claimed',
                    lease_owner = $owner,
                    lease_expires_at = $expires,
                    attempt = attempt + 1,
                    updated_at = $now
                WHERE id = (
                    SELECT id FROM queue_item
                    WHERE status = 'pending'
                      AND available_at <= $now
                      AND attempt < max_attempts
                    ORDER BY priority DESC, created_at ASC, id ASC
                    LIMIT 1
                )
                RETURNING id, workspace_id, task_id, task_hash, status, priority, attempt, max_attempts,
                          preferred_provider, available_at, lease_owner, lease_expires_at,
                          last_error_code, last_error, created_at, updated_at;
                """;
            command.Parameters.AddWithValue("$owner", ownerId);
            command.Parameters.AddWithValue("$expires", expires);
            command.Parameters.AddWithValue("$now", now);

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                item = ReadItem(reader);
            }
        }

        string? attemptId = null;
        if (item is not null)
        {
            attemptId = Guid.NewGuid().ToString("N");
            using var attempt = connection.CreateCommand();
            attempt.Transaction = transaction;
            attempt.CommandText = """
                INSERT INTO attempt (id, queue_item_id, attempt_no, provider, started_at)
                VALUES ($id, $item, $no, $provider, $now);
                """;
            attempt.Parameters.AddWithValue("$id", attemptId);
            attempt.Parameters.AddWithValue("$item", item.Id);
            attempt.Parameters.AddWithValue("$no", item.Attempt);
            attempt.Parameters.AddWithValue("$provider", item.PreferredProvider);
            attempt.Parameters.AddWithValue("$now", now);
            await attempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return item is null || attemptId is null ? null : new ClaimedQueueItem(item, attemptId);
    }

    /// <summary>续租：只有当前持有者能续期。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="ownerId">持有者标识。</param>
    /// <param name="lease">新的租约时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> HeartbeatAsync(
        string queueItemId,
        string ownerId,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queue_item
            SET lease_expires_at = $expires, updated_at = $now
            WHERE id = $id AND lease_owner = $owner AND status IN ('claimed','running');
            """;
        command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.Add(lease).ToString("O"));
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$id", queueItemId);
        command.Parameters.AddWithValue("$owner", ownerId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>回收租约过期的队列项：相关 attempt 记为 abandoned，队列项回到 pending。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被回收的队列项数量。</returns>
    public async Task<int> ReclaimExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = Now();
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        using (var abandon = connection.CreateCommand())
        {
            abandon.Transaction = transaction;
            abandon.CommandText = """
                UPDATE attempt
                SET ended_at = $now, outcome = 'abandoned', error_code = 'LEASE_EXPIRED'
                WHERE ended_at IS NULL AND queue_item_id IN (
                    SELECT id FROM queue_item
                    WHERE status IN ('claimed','running') AND lease_expires_at IS NOT NULL AND lease_expires_at < $now
                );
                """;
            abandon.Parameters.AddWithValue("$now", now);
            await abandon.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int reclaimed;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE queue_item
                SET status = 'pending',
                    lease_owner = NULL,
                    lease_expires_at = NULL,
                    last_error_code = 'LEASE_EXPIRED',
                    last_error = '租约过期，已自动回收',
                    updated_at = $now
                WHERE status IN ('claimed','running') AND lease_expires_at IS NOT NULL AND lease_expires_at < $now;
                """;
            command.Parameters.AddWithValue("$now", now);
            reclaimed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return reclaimed;
    }

    /// <summary>分页查询队列项。顺序固定为 priority 降序、created_at 升序、id 升序，游标基于该顺序的键集分页。</summary>
    /// <param name="status">状态过滤；<c>all</c> 表示不过滤。</param>
    /// <param name="limit">1–100。</param>
    /// <param name="cursor">上一页返回的 nextCursor。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentOutOfRangeException">limit 越界。</exception>
    /// <exception cref="ArgumentException">status 非法。</exception>
    public async Task<QueuePage> ListAsync(string status, int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit 必须在 1–100 之间。");
        }

        if (!string.Equals(status, "all", StringComparison.Ordinal) &&
            !_allowedStatuses.Contains(status, StringComparer.Ordinal))
        {
            throw new ArgumentException($"未知状态：{status}", nameof(status));
        }

        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();

        var sql = new StringBuilder("""
            SELECT id, workspace_id, task_id, task_hash, status, priority, attempt, max_attempts,
                   preferred_provider, available_at, lease_owner, lease_expires_at,
                   last_error_code, last_error, created_at, updated_at
            FROM queue_item
            WHERE 1 = 1
            """);

        if (!string.Equals(status, "all", StringComparison.Ordinal))
        {
            sql.Append(" AND status = $status");
            command.Parameters.AddWithValue("$status", status);
        }

        if (TryDecodeCursor(cursor, out var cursorPriority, out var cursorCreatedAt, out var cursorId))
        {
            sql.Append("""
                 AND (priority < $cp
                      OR (priority = $cp AND (created_at > $cc
                                              OR (created_at = $cc AND id > $ci))))
                """);
            command.Parameters.AddWithValue("$cp", cursorPriority);
            command.Parameters.AddWithValue("$cc", cursorCreatedAt);
            command.Parameters.AddWithValue("$ci", cursorId);
        }

        sql.Append(" ORDER BY priority DESC, created_at ASC, id ASC LIMIT $limit;");
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$limit", limit);

        var items = new List<QueueItem>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadItem(reader));
            }
        }

        var nextCursor = items.Count == limit ? EncodeCursor(items[^1]) : null;
        return new QueuePage(items, nextCursor);
    }

    /// <summary>
    /// 列出"待派发 + 执行中"的队列项（只读资源 <c>devhub://scheduler/queue</c> 使用）。
    /// 顺序与 <see cref="ListAsync"/> 一致，最多返回 <paramref name="limit"/> 条。
    /// </summary>
    /// <param name="limit">最多返回条数（1–100）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<QueueItem>> ListActiveAsync(int limit, CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 100);
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, workspace_id, task_id, task_hash, status, priority, attempt, max_attempts,
                   preferred_provider, available_at, lease_owner, lease_expires_at,
                   last_error_code, last_error, created_at, updated_at
            FROM queue_item
            WHERE status IN ('pending','claimed','running')
            ORDER BY priority DESC, created_at ASC, id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", capped);

        var items = new List<QueueItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    /// <summary>按状态聚合队列计数（摘要与状态快照使用）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, COUNT(*) FROM queue_item GROUP BY status;";

        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counters[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counters;
    }

    /// <summary>列出任务物化视图（只读资源 <c>devhub://tasks</c> 使用）。</summary>
    /// <param name="limit">最多返回条数（1–500）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<TaskListItem>> ListTasksAsync(int limit, CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 500);
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_id, id, title, status, priority, seen_at
            FROM task_view
            ORDER BY workspace_id ASC, priority DESC, id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", capped);

        var items = new List<TaskListItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new TaskListItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5)));
        }

        return items;
    }

    /// <summary>读取单个队列项详情（含任务标题与尝试列表）。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<QueueItemDetail?> GetAsync(string queueItemId, CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        QueueItem? item = null;
        string title = string.Empty;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT q.id, q.workspace_id, q.task_id, q.task_hash, q.status, q.priority, q.attempt, q.max_attempts,
                       q.preferred_provider, q.available_at, q.lease_owner, q.lease_expires_at,
                       q.last_error_code, q.last_error, q.created_at, q.updated_at,
                       COALESCE(t.title, '')
                FROM queue_item q
                LEFT JOIN task_view t ON t.workspace_id = q.workspace_id AND t.id = q.task_id
                WHERE q.id = $id;
                """;
            command.Parameters.AddWithValue("$id", queueItemId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            item = ReadItem(reader);
            title = reader.GetString(16);
        }

        var attempts = new List<AttemptRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, attempt_no, provider, model, started_at, ended_at, outcome, error_kind, error_code,
                       tool_calls, input_tokens, output_tokens, usage_estimated, files_changed, summary
                FROM attempt
                WHERE queue_item_id = $id
                ORDER BY attempt_no DESC;
                """;
            command.Parameters.AddWithValue("$id", queueItemId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                attempts.Add(new AttemptRecord(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    reader.GetInt32(12) == 1,
                    reader.IsDBNull(13) ? null : reader.GetInt32(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14)));
            }
        }

        return new QueueItemDetail(item, title, attempts);
    }

    /// <summary>构造 dedupe_key：<c>workspace:task:contentHash</c>。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="contentHash">内容哈希。</param>
    public static string BuildDedupeKey(string workspaceId, string taskId, string contentHash) =>
        $"{workspaceId}:{taskId}:{contentHash}";

    /// <summary>当前 UTC 时间（ISO-8601，固定 +00:00 偏移，保证字符串可直接比较）。</summary>
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>同一任务已存在的未结束队列项（<c>pending</c>/<c>claimed</c>/<c>running</c>）。</summary>
    /// <param name="Id">队列项 id。</param>
    /// <param name="Provider">该队列项的首选 provider。</param>
    private sealed record ExistingQueueItem(string Id, string Provider);

    /// <summary>查找同一任务未结束的队列项，避免内容变更后重复派发同一任务。</summary>
    /// <returns>已存在的队列项；没有则返回 null。</returns>
    private static async Task<ExistingQueueItem?> FindActiveItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        string taskId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, preferred_provider FROM queue_item
            WHERE workspace_id = $workspace AND task_id = $task AND status IN ('pending','claimed','running')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId);
        command.Parameters.AddWithValue("$task", taskId);

        return await ReadExistingAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 dedupe_key 查已有队列项。</summary>
    /// <returns>命中的队列项；没有则返回 null。</returns>
    private static async Task<ExistingQueueItem?> FindByDedupeKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string dedupeKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, preferred_provider FROM queue_item WHERE dedupe_key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", dedupeKey);

        return await ReadExistingAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取"id + preferred_provider"两列的一行；无行返回 null。</summary>
    /// <param name="command">已配置好 SQL 与参数的查询命令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<ExistingQueueItem?> ReadExistingAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ExistingQueueItem(reader.GetString(0), reader.GetString(1));
    }

    /// <summary>插入队列项；dedupe_key 冲突时返回 false（幂等）。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="transaction">事务。</param>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="task">任务投影。</param>
    /// <param name="provider">已归一化的定时派发 provider。</param>
    /// <param name="dedupeKey">去重键。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<bool> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        TaskRow task,
        string provider,
        string dedupeKey,
        string now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO queue_item
              (id, workspace_id, task_id, task_hash, dedupe_key, status, priority, attempt, max_attempts,
               preferred_provider, available_at, created_at, updated_at)
            VALUES
              ($id, $workspace, $task, $hash, $dedupe, 'pending', $priority, 0, 3, $provider, $now, $now, $now)
            ON CONFLICT (dedupe_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$workspace", workspaceId);
        command.Parameters.AddWithValue("$task", task.Id);
        command.Parameters.AddWithValue("$hash", task.ContentHash);
        command.Parameters.AddWithValue("$dedupe", dedupeKey);
        command.Parameters.AddWithValue("$priority", task.Priority);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$now", now);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>按列序读取队列项。</summary>
    private static QueueItem ReadItem(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.GetString(14),
        reader.GetString(15));

    /// <summary>生成下一页游标（base64url 编码的 priority/createdAt/id）。</summary>
    private static string EncodeCursor(QueueItem item)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CursorPayload(item.Priority, item.CreatedAt, item.Id));
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>解析游标；非法游标视为"从头开始"，避免一个坏游标把查询打死。</summary>
    private static bool TryDecodeCursor(string? cursor, out int priority, out string createdAt, out string id)
    {
        priority = 0;
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

            priority = payload.Priority;
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
    private sealed record CursorPayload(int Priority, string CreatedAt, string Id);
}
