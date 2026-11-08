using System.Security.Cryptography;
using System.Text;
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

    /// <summary>构造去重键：workspace/task/task_hash/question_hash。</summary>
    /// <param name="draft">创建参数。</param>
    private static string BuildDedupeKey(InputRequestDraft draft)
    {
        var questionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draft.Question)))
            .ToLowerInvariant();
        return $"{draft.WorkspaceId}/{draft.TaskId}/{draft.TaskHash}/{questionHash}";
    }
}
