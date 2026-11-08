namespace DevHubd.Core.Queue;

/// <summary>
/// 认领队列项时在该项自身发生的失败（目前为 attempt 写入冲突）。
/// 携带队列项标识，供调度泵按项隔离：推迟该项并继续处理同批其它项，不中断整个 tick。
/// </summary>
public sealed class QueueItemClaimException : Exception
{
    /// <summary>构造带有队列项上下文的认领异常。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="innerException">原始异常。</param>
    public QueueItemClaimException(string queueItemId, string taskId, Exception innerException)
        : base($"队列项 {queueItemId} 认领失败：{innerException.Message}", innerException)
    {
        QueueItemId = queueItemId;
        TaskId = taskId;
    }

    /// <summary>认领失败的队列项 id。</summary>
    public string QueueItemId { get; }

    /// <summary>对应任务 id。</summary>
    public string TaskId { get; }
}
