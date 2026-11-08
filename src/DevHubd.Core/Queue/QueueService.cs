using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using DevHubd.Core.Execution;
using DevHubd.Core.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DevHubd.Core.Queue;

/// <summary>
/// 队列服务：入队（按 dedupe_key 幂等）、原子认领、租约续期与回收、分页查询。
/// 并发安全依赖 SQLite 的 <c>BEGIN IMMEDIATE</c>（写锁前置）+ <c>busy_timeout</c>，
/// 而不是进程内 <c>lock</c>——后者在多连接下不成立。
/// 除原子认领外，其余读写统一通过 EF Core（<see cref="DevHubDbContext"/>）完成。
/// </summary>
public sealed class QueueService
{
    /// <summary>未结束（仍需派发/执行）的队列状态。</summary>
    private static readonly string[] _activeStatuses =
    {
        "pending", "claimed", "running",
    };

    /// <summary>全部合法状态（列表过滤与工具层校验共用）。</summary>
    private static readonly string[] _allowedStatuses =
    {
        "pending", "claimed", "running", "succeeded", "failed", "dead", "cancelled",
    };

    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂（每个方法内创建独立上下文，上下文不跨调用共享）。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public QueueService(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>用裸连接工厂构造（测试与不依赖 DI 的场景）；内部转换为 EF 上下文工厂。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public QueueService(SqliteConnectionFactory factory)
        : this(new DevHubDbContextFactory(factory.ConnectionString))
    {
    }

    /// <summary>读取单个任务（task_view 投影）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<TaskRow?> GetTaskAsync(string workspaceId, string taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.TaskViews
            .AsNoTracking()
            .Where(task => task.WorkspaceId == workspaceId && task.Id == taskId)
            .Select(task => new { task.Id, task.Title, task.Status, task.ContentHash, task.Priority })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new TaskRow(row.Id, row.Title, row.Status, row.ContentHash, row.Priority);
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
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var tasks = await db.TaskViews
            .AsNoTracking()
            .Where(task =>
                task.WorkspaceId == workspaceId &&
                task.Status == "ready" &&
                task.ApprovalState != "approved")
            .OrderByDescending(task => task.Priority)
            .ThenBy(task => task.Id)
            .Select(task => new { task.Id, task.Title, task.Status, task.ContentHash, task.Priority, task.RoleId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 入队闸门以"结果状态"判定，而不是队列行计数：同一任务当前内容哈希若已有
        // awaiting_review / delivered 的结果，就不派发。否则队列项一旦进入终态，
        // 每一轮 tick 都会把同一份内容再跑一次（实测 90 秒内重复执行 4 次）。
        var blockedPairs = (await db.Results
                .AsNoTracking()
                .Where(result => result.Outcome == "awaiting_review" || result.Outcome == "delivered")
                .Join(
                    db.QueueItems.AsNoTracking(),
                    result => result.QueueItemId,
                    item => item.Id,
                    (result, item) => new { result.TaskId, item.TaskHash })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(entry => (entry.TaskId, entry.TaskHash))
            .ToHashSet();

        // 驳回次数取审计（result.rejected）里的持久计数：被驳回的结果 outcome 会变成 failed，
        // 与传输失败产生的 failed 结果无法区分，因此不能直接数 result.outcome。
        var rejectedCounts = await LoadRejectedCountsAsync(db, cancellationToken).ConfigureAwait(false);

        // 已用过的去重键后缀：计数一律 max+1，即使将来清理队列行也不会回退、不会撞旧键。
        var usedKeys = await db.QueueItems
            .AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId)
            .Select(item => new { item.TaskId, item.DedupeKey })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 一次性取回本工作区所有"未结束"的 task_id：任务在 task_view 中天然唯一，
        // 因此快照判断与逐条 EXISTS 等价，但把 N 次查询收敛成 1 次。
        var activeTaskIds = (await db.QueueItems
                .AsNoTracking()
                .Where(item => item.WorkspaceId == workspaceId && _activeStatuses.Contains(item.Status))
                .Select(item => item.TaskId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var enqueued = 0;
        var deduped = 0;
        var skippedActive = 0;

        foreach (var task in tasks)
        {
            if (activeTaskIds.Contains(task.Id))
            {
                skippedActive++;
                continue;
            }

            // 该内容已有待审或已交付结果：不派发，计入去重（否则终态队列行会诱发重复执行）。
            if (blockedPairs.Contains((task.Id, task.ContentHash)))
            {
                deduped++;
                continue;
            }

            if (dryRun)
            {
                enqueued++;
                continue;
            }

            var prefix = RoundPrefix(workspaceId, task.Id, task.ContentHash);
            var used = usedKeys
                .Where(key => string.Equals(key.TaskId, task.Id, StringComparison.Ordinal))
                .Select(key => ParseRound(key.DedupeKey, prefix))
                .DefaultIfEmpty(-1)
                .Max();
            var round = Math.Max(rejectedCounts.GetValueOrDefault(task.Id), used + 1);
            var dedupeKey = BuildDedupeKey(workspaceId, task.Id, task.ContentHash, round);
            var inserted = await InsertIgnoreConflictAsync(
                db,
                workspaceId,
                new TaskRow(task.Id, task.Title, task.Status, task.ContentHash, task.Priority),
                task.RoleId,
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
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new EnqueueResult(enqueued, deduped, skippedActive);
    }

    /// <summary>手动把单个任务入队（<c>dispatch_now</c> 使用，不涉及模型调用）。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="task">任务投影。</param>
    /// <param name="provider">首选 provider。</param>
    /// <param name="dryRun">只判断是否可入队，不写库。</param>
    /// <param name="requestId">可选幂等键；提供时以其作为去重键，同一键重复派发返回首次队列项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ManualEnqueueResult> EnqueueTaskAsync(
        string workspaceId,
        TaskRow task,
        string provider,
        bool dryRun,
        string? requestId,
        CancellationToken cancellationToken = default)
    {
        var now = Now();
        // 幂等键直接作为 dedupe_key：复用它既有的唯一约束与去重分支，避免为请求号新增列。
        var requestDedupeKey = string.IsNullOrWhiteSpace(requestId)
            ? null
            : $"request:{workspaceId}:{requestId.Trim()}";

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 无 requestId 时按任务队列代次去重：同一任务每次派发都产生新代次，
        // 因此驳回后即使任务文件字节未变，也能重新派发。
        // 但若该任务已有进行中的队列项，手动派发仍按幂等处理并返回既有项。
        var dedupeKey = requestDedupeKey;
        if (dedupeKey is null)
        {
            var latest = await db.QueueItems
                .AsNoTracking()
                .Where(item => item.WorkspaceId == workspaceId && item.TaskId == task.Id)
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Select(item => new { item.Id, item.Status, item.PreferredProvider })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (latest is not null &&
                latest.Status is "pending" or "claimed" or "running")
            {
                return new ManualEnqueueResult(false, true, latest.Id, "deduped", latest.PreferredProvider);
            }

            var prefix = RoundPrefix(workspaceId, task.Id, task.ContentHash);
            var usedKeys = await db.QueueItems
                .AsNoTracking()
                .Where(item => item.WorkspaceId == workspaceId && item.TaskId == task.Id)
                .Select(item => item.DedupeKey)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var used = usedKeys
                .Select(key => ParseRound(key, prefix))
                .DefaultIfEmpty(-1)
                .Max();
            var rejected = (await LoadRejectedCountsAsync(db, cancellationToken).ConfigureAwait(false))
                .GetValueOrDefault(task.Id);
            dedupeKey = BuildDedupeKey(workspaceId, task.Id, task.ContentHash, Math.Max(rejected, used + 1));
        }

        var existing = await db.QueueItems
            .AsNoTracking()
            .Where(item => item.DedupeKey == dedupeKey)
            .Select(item => new { item.Id, item.PreferredProvider })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // 返回已有队列项的真实 provider，而不是本次请求的 provider——否则 deduped 会误导调用方。
            return new ManualEnqueueResult(false, true, existing.Id, "deduped", existing.PreferredProvider);
        }

        var active = await db.QueueItems
            .AsNoTracking()
            .Where(item =>
                item.WorkspaceId == workspaceId &&
                item.TaskId == task.Id &&
                _activeStatuses.Contains(item.Status))
            .Select(item => new { item.Id, item.PreferredProvider })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (active is not null)
        {
            return new ManualEnqueueResult(false, true, active.Id, "active_exists", active.PreferredProvider);
        }

        if (dryRun)
        {
            return new ManualEnqueueResult(true, false, null, "would_enqueue", provider);
        }

        var roleId = await db.TaskViews
            .AsNoTracking()
            .Where(view => view.WorkspaceId == workspaceId && view.Id == task.Id)
            .Select(view => view.RoleId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? "implementer";

        var id = Guid.NewGuid().ToString("N");
        db.QueueItems.Add(new QueueItemEntity
        {
            Id = id,
            WorkspaceId = workspaceId,
            TaskId = task.Id,
            TaskHash = task.ContentHash,
            DedupeKey = dedupeKey,
            Status = "pending",
            Priority = task.Priority,
            Attempt = 0,
            MaxAttempts = 3,
            PreferredProvider = provider,
            RoleId = roleId,
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // 并发下两次派发可能计算出同一代次：唯一约束只允许一条落库。
            // 命中已存在行时按去重语义返回，而不是把唯一冲突暴露成内部错误。
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var raced = await db.QueueItems
                .AsNoTracking()
                .Where(item => item.DedupeKey == dedupeKey)
                .Select(item => new { item.Id, item.PreferredProvider })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (raced is null)
            {
                throw;
            }

            return new ManualEnqueueResult(false, true, raced.Id, "deduped", raced.PreferredProvider);
        }

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
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var payload = await db.TaskViews
            .AsNoTracking()
            .Where(task => task.WorkspaceId == workspaceId && task.Id == taskId)
            .Select(task => task.Payload)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

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
    /// 认领条件包含"已消耗重试预算的回合数 &lt; max_attempts"，避免用尽预算的项被反复认领；
    /// 预算口径为 <c>error_code</c> 为空或非 <c>NEEDS_INPUT</c> 的 attempt 数。
    /// </summary>
    /// <remarks>
    /// 这里刻意保留原生 SQL，不能改写成普通 LINQ：
    /// <list type="bullet">
    /// <item>认领要跨进程互斥。若先 <c>SELECT</c> 再 <c>UPDATE</c>，两个进程可能选中同一行（先查后改丢失原子性）；</item>
    /// <item>EF 会把 "查询 → 更新" 翻译成两条语句，无法表达 <c>UPDATE … RETURNING</c> 的单语句原子语义，
    /// 而 <c>UPDATE … RETURNING</c> 本身也不能作为子查询交给 EF 组译。</item>
    /// </list>
    /// 事务仍由 EF 管理：<c>Database.BeginTransactionAsync()</c> 使用 Microsoft.Data.Sqlite 的默认
    /// <c>BEGIN IMMEDIATE</c>（写锁前置），命令通过该事务的连接执行，保证与 attempt 写入同一事务。
    /// </remarks>
    /// <param name="ownerId">认领者标识。</param>
    /// <param name="lease">租约时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="excludedRoles">角色并发已满、本轮不参与认领的角色 id 集合。</param>
    public async Task<ClaimedQueueItem?> ClaimDetailedAsync(
        string ownerId,
        TimeSpan lease,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? excludedRoles = null)
    {
        var nowValue = DateTimeOffset.UtcNow;
        var now = nowValue.ToString("O", CultureInfo.InvariantCulture);
        var expires = nowValue.Add(lease).ToString("O", CultureInfo.InvariantCulture);

        // 角色并发已满时不认领该角色的项：避免"认领后才等待"浪费 attempt，
        // 也不让一个满额角色堵住其它角色。
        var excluded = excludedRoles?
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        var roleClause = excluded.Length == 0
            ? string.Empty
            : $"AND COALESCE(role_id, '{TurnRunner.DefaultRoleId}') NOT IN ({string.Join(", ", excluded.Select((_, index) => $"$excludedRole{index}"))})";

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        QueueItem? item = null;

        // 单语句完成"挑一行 + 占租约 + 递增 attempt"。
        // 注意：这里不能用 Database.SqlQueryRaw/FromSqlRaw 承载 UPDATE … RETURNING——
        // EF 会把它当作需要组译的查询源包一层 SELECT，而该语句对 EF 是"不可组合 SQL"，
        // 运行期会直接抛 InvalidOperationException（已在本仓库实测）。因此命令挂在
        // EF 事务的连接上执行，事务仍由 Database.BeginTransactionAsync() 统一管理。
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = $"""
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
                      -- 重试预算按"非 NEEDS_INPUT 回合数"判定：提问挂起不消耗预算，
                      -- 资源类失败（auth / quota / no_model / bridge_down）由 provider 自身不可用导致，
                      -- 同样不计入预算；其余每个真实回合（含进行中、失败、成功）各计 1 次。
                      AND (
                            SELECT COUNT(*) FROM attempt a
                            WHERE a.queue_item_id = queue_item.id
                              AND (a.error_code IS NULL OR a.error_code <> 'NEEDS_INPUT')
                              AND (a.error_kind IS NULL OR a.error_kind NOT IN ('auth', 'quota', 'no_model', 'bridge_down'))
                          ) < max_attempts
                      AND hold_kind IS NULL
                      {roleClause}
                    ORDER BY priority DESC, created_at ASC, id ASC
                    LIMIT 1
                )
                RETURNING id, workspace_id, task_id, task_hash, status, priority, attempt, max_attempts,
                          preferred_provider, role_id, hold_kind, hold_ref, available_at, lease_owner, lease_expires_at,
                          last_error_code, last_error, created_at, updated_at;
                """;
            AddParameter(command, "$owner", ownerId);
            AddParameter(command, "$expires", expires);
            AddParameter(command, "$now", now);
            for (var index = 0; index < excluded.Length; index++)
            {
                AddParameter(command, $"$excludedRole{index}", excluded[index]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                item = ReadItem(reader);
            }
        }

        string? attemptId = null;
        if (item is not null)
        {
            attemptId = Guid.NewGuid().ToString("N");
            db.Attempts.Add(new AttemptEntity
            {
                Id = attemptId,
                QueueItemId = item.Id,
                AttemptNo = item.Attempt,
                Provider = item.PreferredProvider,
                StartedAt = now,
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception)
            {
                // 单项写入失败必须可被上层按项隔离：带上队列项标识抛出，
                // 由调度泵把该行推迟后继续处理同批其它项，而不是让整个 tick 失败。
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new QueueItemClaimException(item.Id, item.TaskId, exception);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        var expires = DateTimeOffset.UtcNow.Add(lease).ToString("O", CultureInfo.InvariantCulture);
        var now = Now();

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var affected = await db.QueueItems
            .Where(item =>
                item.Id == queueItemId &&
                item.LeaseOwner == ownerId &&
                (item.Status == "claimed" || item.Status == "running"))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.LeaseExpiresAt, expires)
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>回收租约过期的队列项：相关 attempt 记为 abandoned，队列项回到 pending。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被回收的队列项数量。</returns>
    public async Task<int> ReclaimExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = Now();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var expired = db.QueueItems.Where(item =>
            (item.Status == "claimed" || item.Status == "running") &&
            item.LeaseExpiresAt != null &&
            item.LeaseExpiresAt.CompareTo(now) < 0);

        var expiredIds = await expired
            .Select(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expiredIds.Count > 0)
        {
            await db.Attempts
                .Where(attempt => attempt.EndedAt == null && expiredIds.Contains(attempt.QueueItemId))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(attempt => attempt.EndedAt, now)
                        .SetProperty(attempt => attempt.Outcome, "abandoned")
                        .SetProperty(attempt => attempt.ErrorCode, "LEASE_EXPIRED"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var reclaimed = await expired
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "pending")
                    .SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                    .SetProperty(item => item.LastErrorCode, "LEASE_EXPIRED")
                    .SetProperty(item => item.LastError, "租约过期，已自动回收")
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = db.QueueItems.AsNoTracking();

        if (!string.Equals(status, "all", StringComparison.Ordinal))
        {
            query = query.Where(item => item.Status == status);
        }

        if (TryDecodeCursor(cursor, out var cursorPriority, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(item =>
                item.Priority < cursorPriority ||
                (item.Priority == cursorPriority &&
                 (item.CreatedAt.CompareTo(cursorCreatedAt) > 0 ||
                  (item.CreatedAt == cursorCreatedAt && item.Id.CompareTo(cursorId) > 0))));
        }

        var rows = await query
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows.Select(ToDomain).ToList();
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
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.QueueItems
            .AsNoTracking()
            .Where(item => _activeStatuses.Contains(item.Status))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(capped)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDomain).ToList();
    }

    /// <summary>按状态聚合队列计数（摘要与状态快照使用）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.QueueItems
            .AsNoTracking()
            .GroupBy(item => item.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            counters[row.Status] = row.Count;
        }

        return counters;
    }

    /// <summary>列出任务物化视图（只读资源 <c>devhub://tasks</c> 使用）。</summary>
    /// <param name="limit">最多返回条数（1–500）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<TaskListItem>> ListTasksAsync(int limit, CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 500);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.TaskViews
            .AsNoTracking()
            .OrderBy(task => task.WorkspaceId)
            .ThenByDescending(task => task.Priority)
            .ThenBy(task => task.Id)
            .Take(capped)
            .Select(task => new
            {
                task.WorkspaceId,
                task.Id,
                task.Title,
                task.Status,
                task.Priority,
                task.RoleId,
                task.SeenAt,
                task.ApprovalState,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new TaskListItem(
                row.WorkspaceId,
                row.Id,
                row.Title,
                row.Status,
                row.Priority,
                row.RoleId,
                row.SeenAt,
                row.ApprovalState ?? "none"))
            .ToList();
    }

    /// <summary>读取单个队列项详情（含任务标题与尝试列表）。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<QueueItemDetail?> GetAsync(string queueItemId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var row = await db.QueueItems
            .AsNoTracking()
            .Where(item => item.Id == queueItemId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var item = ToDomain(row);

        // task_view 采用 LEFT JOIN 语义：任务行缺失时标题回退空串（与既有 COALESCE 行为一致）。
        var title = await db.TaskViews
            .AsNoTracking()
            .Where(task => task.WorkspaceId == item.WorkspaceId && task.Id == item.TaskId)
            .Select(task => task.Title)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? string.Empty;

        var attempts = await db.Attempts
            .AsNoTracking()
            .Where(attempt => attempt.QueueItemId == queueItemId)
            .OrderByDescending(attempt => attempt.AttemptNo)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new QueueItemDetail(item, title, attempts.Select(ToDomain).ToList());
    }

    /// <summary>
    /// 构造 dedupe_key：<c>workspace:task:&lt;contentHash&gt;:r&lt;已驳回次数&gt;</c>。
    /// 键里带内容哈希，任务实质变更后自然产生新键；已驳回次数保证同一内容每轮驳回后可再次派发，
    /// 与任务文件的字节形态（缩进、换行）无关。
    /// </summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="contentHash">任务内容哈希。</param>
    /// <param name="rejectedCount">该任务已驳回次数（max+1 语义，见调用方）。</param>
    public static string BuildDedupeKey(string workspaceId, string taskId, string contentHash, int rejectedCount) =>
        $"{RoundPrefix(workspaceId, taskId, contentHash)}{rejectedCount}";

    /// <summary>去重键中"轮次"后缀之前的前缀。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="contentHash">任务内容哈希。</param>
    private static string RoundPrefix(string workspaceId, string taskId, string contentHash) =>
        $"{workspaceId}:{taskId}:{contentHash}:r";

    /// <summary>从既有去重键解析轮次；不属于当前前缀的键返回 -1。</summary>
    /// <param name="dedupeKey">既有去重键。</param>
    /// <param name="prefix">当前任务 + 内容哈希的前缀。</param>
    private static int ParseRound(string dedupeKey, string prefix) =>
        dedupeKey.StartsWith(prefix, StringComparison.Ordinal) &&
        int.TryParse(dedupeKey.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var round)
            ? round
            : -1;

    /// <summary>
    /// 统计每个任务的已驳回次数。以审计 <c>result.rejected</c>（subject 为结果 id）为准：
    /// 被驳回的结果 outcome 会变成 <c>failed</c>，与传输失败产生的 failed 无法区分。
    /// </summary>
    /// <param name="db">数据库上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<Dictionary<string, int>> LoadRejectedCountsAsync(
        DevHubDbContext db,
        CancellationToken cancellationToken)
    {
        var rows = await db.Audits
            .AsNoTracking()
            .Where(audit => audit.Action == "result.rejected" && audit.Subject != null)
            .Join(
                db.Results.AsNoTracking(),
                audit => audit.Subject!,
                result => result.Id,
                (audit, result) => result.TaskId)
            .GroupBy(taskId => taskId)
            .Select(group => new { TaskId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(entry => entry.TaskId, entry => entry.Count, StringComparer.Ordinal);
    }

    /// <summary>当前 UTC 时间（ISO-8601，固定 +00:00 偏移，保证字符串可直接比较）。</summary>
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// 幂等插入队列项：<c>ON CONFLICT (dedupe_key) DO NOTHING</c> 是 SQLite 原生语义，
    /// EF 没有等价的 LINQ 写法（唯一约束冲突会抛异常而不是静默跳过），因此保留这一条原生 INSERT。
    /// </summary>
    /// <returns>true 表示确实插入；false 表示命中去重。</returns>
    private static async Task<bool> InsertIgnoreConflictAsync(
        DevHubDbContext db,
        string workspaceId,
        TaskRow task,
        string roleId,
        string provider,
        string dedupeKey,
        string now,
        CancellationToken cancellationToken)
    {
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO queue_item
              (id, workspace_id, task_id, task_hash, dedupe_key, status, priority, attempt, max_attempts,
               preferred_provider, role_id, available_at, created_at, updated_at)
            VALUES
              ({0}, {1}, {2}, {3}, {4}, 'pending', {5}, 0, 3, {6}, {7}, {8}, {8}, {8})
            ON CONFLICT (dedupe_key) DO NOTHING;
            """,
            new object[]
            {
                Guid.NewGuid().ToString("N"),
                workspaceId,
                task.Id,
                task.ContentHash,
                dedupeKey,
                task.Priority,
                provider,
                roleId,
                now,
            },
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>给原生命令追加参数。</summary>
    /// <param name="command">命令。</param>
    /// <param name="name">参数名（含 <c>$</c> 前缀）。</param>
    /// <param name="value">参数值。</param>
    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>把持久化模型映射为领域投影。</summary>
    /// <param name="entity">持久化模型。</param>
    private static QueueItem ToDomain(QueueItemEntity entity) => new(
        entity.Id,
        entity.WorkspaceId,
        entity.TaskId,
        entity.TaskHash,
        entity.Status,
        entity.Priority,
        entity.Attempt,
        entity.MaxAttempts,
        entity.PreferredProvider,
        entity.RoleId,
        entity.HoldKind,
        entity.HoldRef,
        entity.AvailableAt,
        entity.LeaseOwner,
        entity.LeaseExpiresAt,
        entity.LastErrorCode,
        entity.LastError,
        entity.CreatedAt,
        entity.UpdatedAt);

    /// <summary>把持久化模型映射为 attempt 投影。</summary>
    /// <param name="entity">持久化模型。</param>
    private static AttemptRecord ToDomain(AttemptEntity entity) => new(
        entity.Id,
        entity.AttemptNo,
        entity.Provider,
        entity.Model,
        entity.StartedAt,
        entity.EndedAt,
        entity.Outcome,
        entity.ErrorKind,
        entity.ErrorCode,
        entity.ToolCalls,
        entity.InputTokens,
        entity.OutputTokens,
        entity.UsageEstimated,
        entity.UsageAvailable,
        entity.RoundsExhausted,
        entity.FilesChanged,
        entity.Summary);

    /// <summary>按列序读取原生认领语句返回的队列项。</summary>
    /// <param name="reader">数据读取器。</param>
    private static QueueItem ReadItem(DbDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetString(16),
        reader.GetString(17),
        reader.GetString(18));

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
