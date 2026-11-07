using DevHubd.Core.Results;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;

namespace DevHubd.Results;

/// <summary>
/// <see cref="IResultQuerySource"/> 的宿主实现：把 Core 层结果投影映射为对外的 camelCase 契约，
/// 并负责 ack 时的时间戳。工具层不直接依赖 <c>DevHubd.Core</c>。
/// </summary>
public sealed class ResultQuerySource : IResultQuerySource
{
    private readonly ResultStore _store;

    /// <summary>注入结果存取。</summary>
    /// <param name="store">结果存取。</param>
    public ResultQuerySource(ResultStore store)
    {
        _store = store;
    }

    /// <summary>分页列出结果。</summary>
    /// <param name="unreadOnly">true 时只返回未读。</param>
    /// <param name="limit">每页数量。</param>
    /// <param name="cursor">上一页游标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ResultPageResult> ListAsync(
        bool unreadOnly,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var page = await _store.ListAsync(unreadOnly, limit, cursor, cancellationToken).ConfigureAwait(false);
        return new ResultPageResult(page.Items.Select(Map).ToArray(), page.NextCursor);
    }

    /// <summary>把指定结果标记为已读。</summary>
    /// <param name="ids">结果 id 列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ResultAckResult> AckAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        var acknowledged = await _store
            .AckAsync(ids, DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
            .ConfigureAwait(false);
        return new ResultAckResult(acknowledged);
    }

    /// <summary>把所有未读结果标记为已读。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ResultAckResult> AckAllAsync(CancellationToken cancellationToken = default)
    {
        var acknowledged = await _store
            .AckAllAsync(DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
            .ConfigureAwait(false);
        return new ResultAckResult(acknowledged);
    }

    /// <summary>结果投影映射。</summary>
    /// <param name="record">Core 投影。</param>
    private static ResultSummary Map(ResultRecord record) => new(
        record.Id,
        record.TaskId,
        record.QueueItemId,
        record.Provider,
        record.Outcome,
        record.Summary,
        record.Artifacts,
        record.CreatedAt,
        record.ReadAt);
}
