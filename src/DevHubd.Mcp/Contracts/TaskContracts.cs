namespace DevHubd.Mcp.Contracts;

/// <summary><c>task_upsert</c> 的返回体。</summary>
/// <param name="TaskId">任务 id。</param>
/// <param name="Path">写入的任务文件绝对路径。</param>
/// <param name="ContentHash">写入内容的 SHA-256（小写十六进制）。</param>
/// <param name="Queued">相同内容是否已有队列项（扫描时会被去重，不会重复执行）。</param>
public sealed record TaskUpsertResult(
    string TaskId,
    string Path,
    string ContentHash,
    bool Queued);
