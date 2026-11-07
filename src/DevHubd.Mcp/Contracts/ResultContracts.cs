namespace DevHubd.Mcp.Contracts;

/// <summary>结果摘要（工具返回用，camelCase）。</summary>
/// <param name="Id">结果 id。</param>
/// <param name="TaskId">任务 id。</param>
/// <param name="QueueItemId">队列项 id。</param>
/// <param name="Provider">产出结果的 provider。</param>
/// <param name="Outcome">delivered / awaiting_review / failed。</param>
/// <param name="Summary">一段人类可读的摘要。</param>
/// <param name="Artifacts">产物相对路径。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="ReadAt">已读时间；null 表示未读。</param>
public sealed record ResultSummary(
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
public sealed record ResultPageResult(IReadOnlyList<ResultSummary> Items, string? NextCursor);

/// <summary>结果确认（ack）的返回。</summary>
/// <param name="Acknowledged">本次实际标记为已读的条数。</param>
public sealed record ResultAckResult(int Acknowledged);
