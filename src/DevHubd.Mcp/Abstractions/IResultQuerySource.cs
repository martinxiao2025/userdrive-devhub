using DevHubd.Mcp.Contracts;

namespace DevHubd.Mcp.Abstractions;

/// <summary>结果只读查询与已读确认。由宿主实现，工具层只做整形与错误封装。</summary>
public interface IResultQuerySource
{
    /// <summary>分页列出结果，按创建时间倒序。</summary>
    /// <param name="unreadOnly">true 时只返回未读。</param>
    /// <param name="limit">1–100。</param>
    /// <param name="cursor">上一页的 nextCursor。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ResultPageResult> ListAsync(
        bool unreadOnly,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);

    /// <summary>把指定结果标记为已读。</summary>
    /// <param name="ids">结果 id 列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ResultAckResult> AckAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);

    /// <summary>把所有未读结果标记为已读。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ResultAckResult> AckAllAsync(CancellationToken cancellationToken = default);
}
