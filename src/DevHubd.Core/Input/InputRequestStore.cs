using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using DevHubd.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Input;

/// <summary>
/// 问题单存取：创建（同任务只保留一条 open）、任务内容变更时取消旧单、读取（会话侧使用）。
/// <para>
/// 主通道是数据库而不是会话推送：后台回合无人值守，问题必须落在持久化层，
/// 会话侧在任何时刻取回并回答后由调度扫描恢复执行。
/// </para>
/// </summary>
public sealed class InputRequestStore
{
    /// <summary>解析问题单 JSON 时容忍 camelCase / PascalCase（模型侧与人工侧写法不同）。</summary>
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public InputRequestStore(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>用裸连接工厂构造（测试与不依赖 DI 的场景）。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public InputRequestStore(SqliteConnectionFactory factory)
        : this(new DevHubDbContextFactory(factory.ConnectionString))
    {
    }

    /// <summary>
    /// 创建问题单。同一工作区 + 任务已有 <c>open</c> 请求时复用既有 id，不重复插入
    /// （每回合只允许一条待回答请求，重复提问返回既有 requestId）。
    /// </summary>
    /// <param name="draft">创建参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<InputRequestCreateResult> CreateAsync(
        InputRequestDraft draft,
        CancellationToken cancellationToken = default)
    {
        var now = UtcTime.Now();
        var dedupeKey = BuildDedupeKey(draft);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await db.InputRequests
            .AsNoTracking()
            .Where(item =>
                item.WorkspaceId == draft.WorkspaceId &&
                item.TaskId == draft.TaskId &&
                item.Status == "open")
            .OrderBy(item => item.CreatedAt)
            .Select(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new InputRequestCreateResult(existing, Created: false);
        }

        var id = Guid.NewGuid().ToString("N");
        db.InputRequests.Add(new InputRequestEntity
        {
            Id = id,
            WorkspaceId = draft.WorkspaceId,
            TaskId = draft.TaskId,
            QueueItemId = draft.QueueItemId,
            RoleId = draft.RoleId,
            Status = "open",
            Question = draft.Question,
            Context = draft.ContextJson,
            Options = draft.OptionsJson,
            MissingInputs = draft.MissingInputsJson,
            AllowFreeText = draft.AllowFreeText,
            DedupeKey = dedupeKey,
            CreatedAt = now,
            ExpiresAt = UtcTime.Format(draft.ExpiresAt),
        });

        db.Audits.Add(new AuditEntity
        {
            At = now,
            Actor = "devhubd",
            Action = "input.requested",
            Subject = id,
            Detail = $"task={draft.TaskId} queueItem={draft.QueueItemId} role={draft.RoleId}",
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // 并发下另一连接可能刚插入同一 dedupe_key（部分唯一索引只约束 open）。
            // 回滚后复用既有 open 请求；没有既有请求说明是别的写入错误，原样抛出。
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();

            var raced = await db.InputRequests
                .AsNoTracking()
                .Where(item =>
                    item.WorkspaceId == draft.WorkspaceId &&
                    item.TaskId == draft.TaskId &&
                    item.Status == "open")
                .OrderBy(item => item.CreatedAt)
                .Select(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (raced is null)
            {
                throw;
            }

            return new InputRequestCreateResult(raced, Created: false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new InputRequestCreateResult(id, Created: true);
    }

    /// <summary>
    /// 取消"任务内容已变更"的旧问题单：比较问题单所属队列项的 <c>task_hash</c> 与
    /// <c>task_view.content_hash</c>，不一致（或任务已消失）即取消，并终止对应挂起队列项
    /// （<c>last_error_code=TASK_CHANGED</c>），避免用旧答案回答新任务、也避免旧挂起项永久占位。
    /// </summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被取消的问题单数量。</returns>
    public async Task<int> CancelStaleAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var now = UtcTime.Now();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var open = await db.InputRequests
            .AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.Status == "open")
            .Select(item => new { item.Id, item.QueueItemId, item.TaskId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (open.Count == 0)
        {
            return 0;
        }

        var queueItemIds = open.Select(item => item.QueueItemId).Distinct(StringComparer.Ordinal).ToList();
        var heldHashes = await db.QueueItems
            .AsNoTracking()
            .Where(item => queueItemIds.Contains(item.Id))
            .Select(item => new { item.Id, item.TaskHash })
            .ToDictionaryAsync(item => item.Id, item => item.TaskHash, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        var taskIds = open.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).ToList();
        var currentHashes = await db.TaskViews
            .AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && taskIds.Contains(item.Id))
            .Select(item => new { item.Id, item.ContentHash })
            .ToDictionaryAsync(item => item.Id, item => item.ContentHash, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        var stale = open
            .Where(item =>
                !heldHashes.TryGetValue(item.QueueItemId, out var heldHash) ||
                !currentHashes.TryGetValue(item.TaskId, out var currentHash) ||
                !string.Equals(heldHash, currentHash, StringComparison.Ordinal))
            .ToList();

        if (stale.Count == 0)
        {
            return 0;
        }

        var staleIds = stale.Select(item => item.Id).ToList();
        var staleQueueItemIds = stale.Select(item => item.QueueItemId).Distinct(StringComparer.Ordinal).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await db.InputRequests
            .Where(item => staleIds.Contains(item.Id))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.Status, "cancelled"),
                cancellationToken)
            .ConfigureAwait(false);

        await db.QueueItems
            .Where(item =>
                staleQueueItemIds.Contains(item.Id) &&
                item.HoldKind != null &&
                item.HoldRef != null &&
                staleIds.Contains(item.HoldRef))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "cancelled")
                    .SetProperty(item => item.HoldKind, (string?)null)
                    .SetProperty(item => item.HoldRef, (string?)null)
                    .SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                    .SetProperty(item => item.LastErrorCode, "TASK_CHANGED")
                    .SetProperty(item => item.LastError, "任务内容已变更，旧的问题单已取消。")
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in stale)
        {
            db.Audits.Add(new AuditEntity
            {
                At = now,
                Actor = "devhubd",
                Action = "input.cancelled",
                Subject = item.Id,
                Detail = "reason=TASK_CHANGED",
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stale.Count;
    }

    /// <summary>分页列出问题单，按创建时间倒序；<paramref name="status"/> 支持 <c>all</c>。</summary>
    /// <param name="workspaceId">按工作区过滤；null 表示不限。</param>
    /// <param name="taskId">按任务过滤；null 表示不限。</param>
    /// <param name="status">open / answered / expired / cancelled / all。</param>
    /// <param name="limit">每页数量 1–100。</param>
    /// <param name="cursor">上一页返回的 nextCursor。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<InputRequestPage> ListAsync(
        string? workspaceId,
        string? taskId,
        string status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit 必须在 1–100 之间。");
        }

        if (!IsKnownStatus(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "status 必须是 open/answered/expired/cancelled/all。");
        }

        if (!TryDecodeCursor(cursor, out var cursorCreatedAt, out var cursorId))
        {
            throw new ArgumentException("cursor 非法。", nameof(cursor));
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.InputRequests.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            query = query.Where(item => item.WorkspaceId == workspaceId);
        }

        if (!string.IsNullOrWhiteSpace(taskId))
        {
            query = query.Where(item => item.TaskId == taskId);
        }

        if (!string.Equals(status, "all", StringComparison.Ordinal))
        {
            query = query.Where(item => item.Status == status);
        }

        if (cursorCreatedAt is not null)
        {
            query = query.Where(item =>
                item.CreatedAt.CompareTo(cursorCreatedAt) < 0 ||
                (item.CreatedAt == cursorCreatedAt && item.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(item => new InputRequestRow(
                item.Id,
                item.WorkspaceId,
                item.TaskId,
                item.QueueItemId,
                item.RoleId,
                item.Status,
                item.Question,
                ParseStringArray(item.MissingInputs),
                ParseOptions(item.Options).Count,
                UtcTime.NormalizeRequired(item.CreatedAt),
                UtcTime.NormalizeRequired(item.ExpiresAt),
                UtcTime.Normalize(item.AnsweredAt)))
            .ToArray();

        var nextCursor = rows.Count == limit
            ? EncodeCursor(rows[^1].CreatedAt, rows[^1].Id)
            : null;

        return new InputRequestPage(items, nextCursor);
    }

    /// <summary>读取问题单详情（含同任务的其他问题单）；不存在返回 null。</summary>
    /// <param name="id">问题单 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<InputRequestDetail?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.InputRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        var related = await db.InputRequests
            .AsNoTracking()
            .Where(item =>
                item.Id != entity.Id &&
                item.WorkspaceId == entity.WorkspaceId &&
                item.TaskId == entity.TaskId)
            .OrderByDescending(item => item.CreatedAt)
            .Take(5)
            .Select(item => new InputRequestRelated(item.Id, item.Status, item.Question, item.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new InputRequestDetail(
            entity.Id,
            entity.WorkspaceId,
            entity.TaskId,
            entity.QueueItemId,
            entity.RoleId,
            entity.Status,
            entity.Question,
            entity.Context,
            ParseOptions(entity.Options),
            ParseStringArray(entity.MissingInputs),
            entity.AllowFreeText,
            UtcTime.NormalizeRequired(entity.CreatedAt),
            UtcTime.NormalizeRequired(entity.ExpiresAt),
            UtcTime.Normalize(entity.AnsweredAt),
            entity.Answer,
            entity.AnswerOptionId,
            entity.AnswerSource,
            entity.AnsweredBy,
            related.Select(item => item with { CreatedAt = UtcTime.NormalizeRequired(item.CreatedAt) }).ToArray());
    }

    /// <summary>
    /// 回答问题：事务内更新问题单并清除队列项挂起（<c>available_at=now</c>），
    /// 由调度扫描在下一轮认领恢复。相同答案重复提交幂等返回首次结果；
    /// 不同答案返回 <see cref="InputAnswerStatus.Conflict"/> 且不覆盖。
    /// </summary>
    /// <param name="id">问题单 id。</param>
    /// <param name="answer">回答文本（1–4000 字符）。</param>
    /// <param name="optionId">选中的选项 id；必须属于该问题单。</param>
    /// <param name="answeredBy">回答者标识。</param>
    /// <param name="idempotencyKey">调用方幂等键（仅写入审计便于追踪；幂等本身由答案相等保证）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<InputAnswerOutcome> AnswerAsync(
        string id,
        string answer,
        string? optionId,
        string? answeredBy,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var now = UtcTime.Now();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var entity = await db.InputRequests
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return new InputAnswerOutcome(InputAnswerStatus.NotFound, id, null, null, "问题单不存在。");
        }

        if (string.Equals(entity.Status, "answered", StringComparison.Ordinal))
        {
            var sameAnswer = string.Equals(entity.Answer, answer, StringComparison.Ordinal) &&
                             string.Equals(entity.AnswerOptionId, optionId, StringComparison.Ordinal);
            return sameAnswer
                ? new InputAnswerOutcome(
                    InputAnswerStatus.Duplicate,
                    entity.Id,
                    entity.QueueItemId,
                    UtcTime.Normalize(entity.AnsweredAt),
                    null)
                : new InputAnswerOutcome(
                    InputAnswerStatus.Conflict,
                    entity.Id,
                    null,
                    UtcTime.Normalize(entity.AnsweredAt),
                    "问题单已回答，答案不可覆盖。");
        }

        if (!string.Equals(entity.Status, "open", StringComparison.Ordinal))
        {
            return new InputAnswerOutcome(
                InputAnswerStatus.Closed,
                entity.Id,
                null,
                null,
                $"问题单状态为 {entity.Status}，不能再回答。");
        }

        if (string.CompareOrdinal(entity.ExpiresAt, now) < 0)
        {
            await ExpireAsync(db, entity, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new InputAnswerOutcome(InputAnswerStatus.Closed, entity.Id, null, null, "问题单已过期。");
        }

        var trimmed = answer?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 4000)
        {
            return new InputAnswerOutcome(
                InputAnswerStatus.InvalidAnswer,
                entity.Id,
                null,
                null,
                "answer 必须在 1–4000 字符之间。");
        }

        if (optionId is not null && !ParseOptions(entity.Options).Any(item => string.Equals(item.Id, optionId, StringComparison.Ordinal)))
        {
            return new InputAnswerOutcome(
                InputAnswerStatus.InvalidAnswer,
                entity.Id,
                null,
                null,
                $"optionId 不在该问题单的选项中：{optionId}");
        }

        entity.Status = "answered";
        entity.Answer = trimmed;
        entity.AnswerOptionId = optionId;
        entity.AnswerSource = "user";
        entity.AnsweredBy = string.IsNullOrWhiteSpace(answeredBy) ? "mcp" : answeredBy;
        entity.AnsweredAt = now;

        var resumed = await db.QueueItems
            .Where(item =>
                item.Id == entity.QueueItemId &&
                item.Status == "pending" &&
                item.HoldKind == "input" &&
                item.HoldRef == entity.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.HoldKind, (string?)null)
                    .SetProperty(item => item.HoldRef, (string?)null)
                    .SetProperty(item => item.AvailableAt, now)
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        db.Audits.Add(new AuditEntity
        {
            At = now,
            Actor = entity.AnsweredBy!,
            Action = "input.answered",
            Subject = entity.Id,
            Detail = $"option={optionId ?? "-"} source=user idem={Truncate(idempotencyKey, 64)}",
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new InputAnswerOutcome(
            InputAnswerStatus.Answered,
            entity.Id,
            resumed > 0 ? entity.QueueItemId : null,
            now,
            null);
    }

    /// <summary>
    /// 撤销问题单：问题单置 <c>cancelled</c>，挂起队列项一并终止
    /// （<c>last_error_code=INPUT_REQUEST_CLOSED</c>），任务不会在无输入的情况下继续跑。
    /// </summary>
    /// <param name="id">问题单 id。</param>
    /// <param name="reason">撤销原因（写入队列项与审计）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<InputCancelOutcome> CancelAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var now = UtcTime.Now();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var entity = await db.InputRequests
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return new InputCancelOutcome(InputCancelStatus.NotFound, id, string.Empty, null, "问题单不存在。");
        }

        if (!string.Equals(entity.Status, "open", StringComparison.Ordinal))
        {
            return new InputCancelOutcome(
                InputCancelStatus.Closed,
                entity.Id,
                entity.QueueItemId,
                null,
                $"问题单状态为 {entity.Status}，不能撤销。");
        }

        entity.Status = "cancelled";

        var detail = string.IsNullOrWhiteSpace(reason) ? "reason=-" : $"reason={reason.Trim()}";
        await db.QueueItems
            .Where(item => item.Id == entity.QueueItemId && item.HoldRef == entity.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "cancelled")
                    .SetProperty(item => item.HoldKind, (string?)null)
                    .SetProperty(item => item.HoldRef, (string?)null)
                    .SetProperty(item => item.LastErrorCode, DevHubErrorCodes.InputRequestClosed)
                    .SetProperty(item => item.LastError, "问题单已被人工撤销。")
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        db.Audits.Add(new AuditEntity
        {
            At = now,
            Actor = "mcp",
            Action = "input.cancelled",
            Subject = entity.Id,
            Detail = detail,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new InputCancelOutcome(InputCancelStatus.Cancelled, entity.Id, entity.QueueItemId, now, null);
    }

    /// <summary>
    /// 读取同一队列项已回答的问题单，按提问时间升序返回，供恢复回合把答案注入提示词。
    /// 只取 <c>answered</c>；已取消 / 已过期的问题不进入提示词。
    /// </summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<AnsweredInput>> ListAnsweredForQueueItemAsync(
        string queueItemId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.InputRequests
            .AsNoTracking()
            .Where(item => item.QueueItemId == queueItemId && item.Status == "answered")
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(item => new AnsweredInput(
                item.Id,
                item.Question,
                ParseOptions(item.Options),
                item.Answer,
                item.AnswerOptionId,
                item.CreatedAt,
                item.AnsweredAt,
                item.AnsweredBy))
            .ToArray();
    }

    /// <summary>
    /// 过期扫描：把 <c>expires_at</c> 已过的 open 问题单置 <c>expired</c>，
    /// 挂起队列项置 <c>failed</c>（<c>INPUT_TIMEOUT</c>）并写一条通知。
    /// </summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次过期的问题单数量。</returns>
    public async Task<int> ExpireOverdueAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var now = UtcTime.Now();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var overdue = await db.InputRequests
            .Where(item =>
                item.WorkspaceId == workspaceId &&
                item.Status == "open" &&
                item.ExpiresAt.CompareTo(now) < 0)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (overdue.Count == 0)
        {
            return 0;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entity in overdue)
        {
            await ExpireAsync(db, entity, now, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return overdue.Count;
    }

    /// <summary>把一个 open 问题单置为过期，并终止其挂起队列项、写通知与审计。</summary>
    /// <param name="db">上下文。</param>
    /// <param name="entity">问题单实体。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task ExpireAsync(
        DevHubDbContext db,
        InputRequestEntity entity,
        string now,
        CancellationToken cancellationToken)
    {
        entity.Status = "expired";

        await db.QueueItems
            .Where(item => item.Id == entity.QueueItemId && item.HoldRef == entity.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "failed")
                    .SetProperty(item => item.HoldKind, (string?)null)
                    .SetProperty(item => item.HoldRef, (string?)null)
                    .SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                    .SetProperty(item => item.LastErrorCode, DevHubErrorCodes.InputTimeout)
                    .SetProperty(item => item.LastError, "等待回答超时。")
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        db.Notices.Add(new NoticeEntity
        {
            Kind = "failure",
            TaskId = entity.TaskId,
            Title = $"需要输入超时：任务 {entity.TaskId}",
            Body = entity.Question,
            DedupeKey = $"input-timeout:{entity.Id}",
            CreatedAt = now,
        });

        db.Audits.Add(new AuditEntity
        {
            At = now,
            Actor = "devhubd",
            Action = "input.expired",
            Subject = entity.Id,
            Detail = "reason=INPUT_TIMEOUT",
        });
    }

    /// <summary>判断状态词是否受支持。</summary>
    /// <param name="status">状态词。</param>
    private static bool IsKnownStatus(string status) =>
        status is "open" or "answered" or "expired" or "cancelled" or "all";

    /// <summary>截断审计用文本，避免超长键污染审计表。</summary>
    /// <param name="value">原值。</param>
    /// <param name="maxLength">最大长度。</param>
    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? "-" : value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>解析选项 JSON；缺失或非法时返回空数组（列表与详情都按“无选项”处理）。</summary>
    /// <param name="json">JSON 文本。</param>
    private static IReadOnlyList<InputOption> ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<InputOption>();
        }

        try
        {
            return JsonSerializer.Deserialize<InputOption[]>(json, _jsonOptions) ?? Array.Empty<InputOption>();
        }
        catch (JsonException)
        {
            return Array.Empty<InputOption>();
        }
    }

    /// <summary>解析字符串数组 JSON；缺失或非法时返回空数组。</summary>
    /// <param name="json">JSON 文本。</param>
    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>编码游标（与 result_list 同构：base64url(createdAt, id)）。</summary>
    /// <param name="createdAt">创建时间。</param>
    /// <param name="id">问题单 id。</param>
    private static string EncodeCursor(string createdAt, string id)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { createdAt, id });
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>解析游标；空游标视为首页，非法游标返回 false。</summary>
    /// <param name="cursor">游标。</param>
    /// <param name="createdAt">解析出的创建时间；首页为 null。</param>
    /// <param name="id">解析出的 id；首页为空串。</param>
    private static bool TryDecodeCursor(string? cursor, out string? createdAt, out string id)
    {
        createdAt = null;
        id = string.Empty;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - (base64.Length % 4)) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(base64));
            var root = document.RootElement;
            if (!root.TryGetProperty("createdAt", out var createdAtElement) ||
                !root.TryGetProperty("id", out var idElement))
            {
                return false;
            }

            createdAt = createdAtElement.GetString();
            id = idElement.GetString() ?? string.Empty;
            return !string.IsNullOrEmpty(createdAt) && id.Length > 0;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>构造去重键：workspace/task/task_hash/question_hash。</summary>
    /// <param name="draft">创建参数。</param>
    private static string BuildDedupeKey(InputRequestDraft draft)
    {
        var questionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draft.Question)))
            .ToLowerInvariant();
        return $"{draft.WorkspaceId}/{draft.TaskId}/{draft.TaskHash}/{questionHash}";
    }
}
