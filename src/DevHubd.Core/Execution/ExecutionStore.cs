using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Execution;

/// <summary>一次 attempt 的终态写入参数。</summary>
/// <param name="Outcome">succeeded / failed / abandoned。</param>
/// <param name="Model">实际使用的模型。</param>
/// <param name="ErrorKind">错误分类。</param>
/// <param name="ErrorCode">错误码。</param>
/// <param name="ToolCalls">工具调用次数。</param>
/// <param name="InputTokens">输入 token；provider 未返回时为 null。</param>
/// <param name="OutputTokens">输出 token；provider 未返回时为 null。</param>
/// <param name="UsageEstimated">true 表示 token 是按字符数估算的近似值，不是 provider 返回的真实 token。</param>
/// <param name="FilesChanged">改动文件数。</param>
/// <param name="Summary">摘要。</param>
public sealed record AttemptCompletion(
    string Outcome,
    string? Model,
    string? ErrorKind,
    string? ErrorCode,
    int ToolCalls,
    int? InputTokens,
    int? OutputTokens,
    bool UsageEstimated,
    int FilesChanged,
    string Summary);

/// <summary>写入结果表的参数。</summary>
/// <param name="TaskId">任务 id。</param>
/// <param name="QueueItemId">队列项 id。</param>
/// <param name="Provider">provider。</param>
/// <param name="Outcome">delivered / awaiting_review / failed。</param>
/// <param name="Summary">摘要。</param>
/// <param name="Artifacts">产物列表（相对路径）。</param>
public sealed record ResultDraft(
    string TaskId,
    string QueueItemId,
    string Provider,
    string Outcome,
    string Summary,
    IReadOnlyList<string> Artifacts);

/// <summary>
/// 执行结果存储：attempt 终态、队列状态迁移、result 与 notice 落库。
/// 与 <c>QueueService</c> 分开，避免队列模块同时承担模型语义。
/// 读写统一通过 EF Core（<see cref="DevHubDbContext"/>）完成；仅在 SQLite 原生 upsert
/// （<c>ON CONFLICT … DO NOTHING</c>）无法用 LINQ 表达时保留单条原生 INSERT。
/// </summary>
public sealed class ExecutionStore
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public ExecutionStore(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>用裸连接工厂构造（测试与不依赖 DI 的场景）。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public ExecutionStore(SqliteConnectionFactory factory)
        : this(new DevHubDbContextFactory(factory.ConnectionString))
    {
    }

    /// <summary>把队列项置为 running（认领后的执行开始）。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task MarkRunningAsync(
        string queueItemId,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.QueueItems
            .Where(item => item.Id == queueItemId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "running")
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>写入 attempt 终态。</summary>
    /// <param name="attemptId">attempt id。</param>
    /// <param name="completion">终态参数。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task CompleteAttemptAsync(
        string attemptId,
        AttemptCompletion completion,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var attempt = await db.Attempts
            .FirstOrDefaultAsync(item => item.Id == attemptId, cancellationToken)
            .ConfigureAwait(false);

        if (attempt is null)
        {
            return;
        }

        attempt.EndedAt = now;
        attempt.Outcome = completion.Outcome;
        // 与既有 COALESCE($model, model) 等价：只有确实拿到模型名时才覆盖。
        attempt.Model = completion.Model ?? attempt.Model;
        attempt.ErrorKind = completion.ErrorKind;
        attempt.ErrorCode = completion.ErrorCode;
        attempt.ToolCalls = completion.ToolCalls;
        attempt.InputTokens = completion.InputTokens;
        attempt.OutputTokens = completion.OutputTokens;
        attempt.UsageEstimated = completion.UsageEstimated;
        attempt.FilesChanged = completion.FilesChanged;
        attempt.Summary = completion.Summary;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把队列项置为终态（succeeded / dead / cancelled）。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="status">终态。</param>
    /// <param name="errorCode">错误码；成功时为 null。</param>
    /// <param name="lastError">错误说明；成功时为 null。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SetTerminalAsync(
        string queueItemId,
        string status,
        string? errorCode,
        string? lastError,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.QueueItems
            .Where(item => item.Id == queueItemId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, status)
                    .SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                    .SetProperty(item => item.LastErrorCode, errorCode)
                    .SetProperty(item => item.LastError, lastError)
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 把队列项放回 pending（可重试或资源类错误退避）。
    /// <paramref name="decrementAttempt"/> 为 true 时同时回退 attempt，表达"资源类失败不消耗重试预算"。
    /// </summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="availableAt">下次可认领时间。</param>
    /// <param name="errorCode">错误码。</param>
    /// <param name="lastError">错误说明。</param>
    /// <param name="decrementAttempt">是否回退 attempt。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RescheduleAsync(
        string queueItemId,
        string availableAt,
        string errorCode,
        string lastError,
        bool decrementAttempt,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var target = db.QueueItems.Where(item => item.Id == queueItemId);

        if (decrementAttempt)
        {
            // CASE WHEN attempt > 0 THEN attempt - 1 ELSE attempt END 交给 SQL 求值，
            // 避免"先读后写"带来的并发窗口。
            await target
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, "pending")
                        .SetProperty(item => item.LeaseOwner, (string?)null)
                        .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                        .SetProperty(item => item.AvailableAt, availableAt)
                        .SetProperty(item => item.LastErrorCode, errorCode)
                        .SetProperty(item => item.LastError, lastError)
                        .SetProperty(item => item.Attempt, item => item.Attempt > 0 ? item.Attempt - 1 : item.Attempt)
                        .SetProperty(item => item.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await target
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, "pending")
                    .SetProperty(item => item.LeaseOwner, (string?)null)
                    .SetProperty(item => item.LeaseExpiresAt, (string?)null)
                    .SetProperty(item => item.AvailableAt, availableAt)
                    .SetProperty(item => item.LastErrorCode, errorCode)
                    .SetProperty(item => item.LastError, lastError)
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>写入一条结果，返回结果 id。</summary>
    /// <param name="draft">结果内容。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<string> InsertResultAsync(
        ResultDraft draft,
        string now,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        db.Results.Add(new ResultEntity
        {
            Id = id,
            TaskId = draft.TaskId,
            QueueItemId = draft.QueueItemId,
            Provider = draft.Provider,
            Outcome = draft.Outcome,
            Summary = draft.Summary,
            Artifacts = JsonSerializer.Serialize(draft.Artifacts),
            CreatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>写入一条通知；dedupe_key 冲突时静默跳过（同一状态进入只提醒一次）。</summary>
    /// <param name="dedupeKey">去重键：kind:subject:state:attempt。</param>
    /// <param name="kind">result / failure / breaker / auth / provider。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="title">标题。</param>
    /// <param name="body">正文。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task InsertNoticeAsync(
        string dedupeKey,
        string kind,
        string? taskId,
        string title,
        string body,
        string now,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // 去重必须由数据库判定（多进程/多连接下"先查后插"会丢幂等），
        // 而 EF 的 LINQ 没有 ON CONFLICT DO NOTHING 等价写法，故保留这一条原生 INSERT。
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO notice (dedupe_key, kind, task_id, title, body, created_at)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5})
            ON CONFLICT (dedupe_key) DO NOTHING;
            """,
            // null 值直接以 null 传递：EF 会为无类型参数生成 NULL，不需要（也不应）传 DBNull。
            new object[] { dedupeKey, kind, taskId!, title, body, now },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>统计当前 running 数量（观测与测试用）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<int> CountRunningAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.QueueItems
            .AsNoTracking()
            .CountAsync(
                item => item.Status == "claimed" || item.Status == "running",
                cancellationToken)
            .ConfigureAwait(false);
    }
}
