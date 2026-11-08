using System.Text.Json;
using DevHubd.Core.Data;
using Microsoft.Data.Sqlite;

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
/// </summary>
public sealed class ExecutionStore
{
    private readonly SqliteConnectionFactory _factory;

    /// <summary>注入连接工厂。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public ExecutionStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
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
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE queue_item SET status = 'running', updated_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", queueItemId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE attempt
            SET ended_at = $now,
                outcome = $outcome,
                model = COALESCE($model, model),
                error_kind = $kind,
                error_code = $code,
                tool_calls = $toolCalls,
                input_tokens = $inputTokens,
                output_tokens = $outputTokens,
                usage_estimated = $usageEstimated,
                files_changed = $filesChanged,
                summary = $summary
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$outcome", completion.Outcome);
        command.Parameters.AddWithValue("$model", (object?)completion.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", (object?)completion.ErrorKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$code", (object?)completion.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolCalls", completion.ToolCalls);
        command.Parameters.AddWithValue("$inputTokens", (object?)completion.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$outputTokens", (object?)completion.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$usageEstimated", completion.UsageEstimated ? 1 : 0);
        command.Parameters.AddWithValue("$filesChanged", completion.FilesChanged);
        command.Parameters.AddWithValue("$summary", completion.Summary);
        command.Parameters.AddWithValue("$id", attemptId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queue_item
            SET status = $status,
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_error_code = $code,
                last_error = $error,
                updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$code", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)lastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", queueItemId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE queue_item
            SET status = 'pending',
                lease_owner = NULL,
                lease_expires_at = NULL,
                available_at = $available,
                last_error_code = $code,
                last_error = $error,
                attempt = CASE WHEN {(decrementAttempt ? 1 : 0)} = 1 AND attempt > 0
                               THEN attempt - 1 ELSE attempt END,
                updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$available", availableAt);
        command.Parameters.AddWithValue("$code", errorCode);
        command.Parameters.AddWithValue("$error", lastError);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", queueItemId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO result (id, task_id, queue_item_id, provider, outcome, summary, artifacts, created_at)
            VALUES ($id, $task, $item, $provider, $outcome, $summary, $artifacts, $now);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$task", draft.TaskId);
        command.Parameters.AddWithValue("$item", draft.QueueItemId);
        command.Parameters.AddWithValue("$provider", draft.Provider);
        command.Parameters.AddWithValue("$outcome", draft.Outcome);
        command.Parameters.AddWithValue("$summary", draft.Summary);
        command.Parameters.AddWithValue("$artifacts", JsonSerializer.Serialize(draft.Artifacts));
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notice (dedupe_key, kind, task_id, title, body, created_at)
            VALUES ($key, $kind, $task, $title, $body, $now)
            ON CONFLICT (dedupe_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$key", dedupeKey);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>统计当前 running 数量（观测与测试用）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<int> CountRunningAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM queue_item WHERE status IN ('claimed','running');";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
