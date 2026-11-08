using DevHubd.Core.Models;
using DevHubd.Core.Queue;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;

namespace DevHubd.Queue;

/// <summary>
/// <see cref="IQueueQuerySource"/> 的宿主实现：把 Core 层队列投影映射为对外的 camelCase 契约。
/// 工具层不直接依赖 <c>DevHubd.Core</c>，依赖方向保持 DevHubd → DevHubd.Mcp → DevHubd.Core。
/// </summary>
public sealed class QueueQuerySource : IQueueQuerySource
{
    private readonly QueueService _queue;

    /// <summary>注入队列服务。</summary>
    /// <param name="queue">队列服务。</param>
    public QueueQuerySource(QueueService queue)
    {
        _queue = queue;
    }

    /// <summary>分页列出队列项。</summary>
    /// <param name="status">状态过滤；<c>all</c> 表示不过滤。</param>
    /// <param name="limit">每页数量。</param>
    /// <param name="cursor">上一页游标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<QueuePageResult> ListAsync(
        string status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var page = await _queue.ListAsync(status, limit, cursor, cancellationToken).ConfigureAwait(false);
        return new QueuePageResult(page.Items.Select(Map).ToArray(), page.NextCursor);
    }

    /// <summary>读取单个队列项详情。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<QueueItemDetailResult?> GetAsync(string queueItemId, CancellationToken cancellationToken = default)
    {
        var detail = await _queue.GetAsync(queueItemId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return null;
        }

        return new QueueItemDetailResult(
            Map(detail.Item),
            detail.TaskTitle,
            detail.Attempts.Select(Map).ToArray());
    }

    /// <summary>列出待派发与执行中的队列项。</summary>
    /// <param name="limit">最多返回条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<QueueItemSummary>> ListActiveAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var items = await _queue.ListActiveAsync(limit, cancellationToken).ConfigureAwait(false);
        return items.Select(Map).ToArray();
    }

    /// <summary>列出任务物化视图。</summary>
    /// <param name="limit">最多返回条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<TaskListItemResult>> ListTasksAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var items = await _queue.ListTasksAsync(limit, cancellationToken).ConfigureAwait(false);
        return items
            .Select(item => new TaskListItemResult(
                item.WorkspaceId,
                item.Id,
                item.Title,
                item.Status,
                item.Priority,
                item.RoleId,
                UtcTime.Normalize(item.SeenAt)!))
            .ToArray();
    }

    /// <summary>队列项投影映射。</summary>
    /// <param name="item">Core 投影。</param>
    private static QueueItemSummary Map(QueueItem item) => new(
        item.Id,
        item.WorkspaceId,
        item.TaskId,
        item.Status,
        item.Priority,
        item.Attempt,
        item.MaxAttempts,
        item.LeaseOwner,
        UtcTime.Normalize(item.LeaseExpiresAt),
        item.LastErrorCode,
        UtcTime.NormalizeRequired(item.CreatedAt),
        UtcTime.NormalizeRequired(item.UpdatedAt));

    /// <summary>执行尝试投影映射。</summary>
    /// <param name="attempt">Core 投影。</param>
    private static AttemptSummary Map(AttemptRecord attempt) => new(
        attempt.AttemptNo,
        attempt.Provider,
        attempt.Model,
        UtcTime.NormalizeRequired(attempt.StartedAt),
        UtcTime.Normalize(attempt.EndedAt),
        attempt.Outcome,
        attempt.ErrorCode,
        attempt.ToolCalls,
        attempt.InputTokens,
        attempt.OutputTokens,
        attempt.InputTokens is null && attempt.OutputTokens is null
            ? null
            : (attempt.InputTokens ?? 0) + (attempt.OutputTokens ?? 0),
        attempt.UsageEstimated,
        attempt.UsageAvailable,
        attempt.RoundsExhausted);
}
