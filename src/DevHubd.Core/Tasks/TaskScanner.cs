using DevHubd.Core.Data;
using DevHubd.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DevHubd.Core.Tasks;

/// <summary>一次工作区扫描的结果。</summary>
/// <param name="Scanned">尝试读取的 .json 文件数。</param>
/// <param name="Accepted">通过校验并写入 task_view 的任务数。</param>
/// <param name="Invalid">校验失败被跳过的文件数。</param>
/// <param name="MarkedMissing">被标记为 missing 的任务数。</param>
/// <param name="Problems">每个非法文件的原因（供宿主记 Warning）。</param>
public sealed record TaskScanResult(int Scanned, int Accepted, int Invalid, int MarkedMissing, IReadOnlyList<string> Problems);

/// <summary>
/// 扫描器：把 <c>&lt;workspace&gt;/.devhub/tasks/*.json</c> 物化到 <c>task_view</c>。
/// 只读任务文件，不改工作区；同一文件重复扫描是幂等的（按 <c>(workspace_id, id)</c> upsert）。
/// 文件消失时把既有行标记为 <c>missing</c>，不删除历史（保留审计）。
/// </summary>
public sealed class TaskScanner
{
    private readonly SqliteConnectionFactory _factory;

    /// <summary>注入连接工厂。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public TaskScanner(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>扫描一个工作区并落库。</summary>
    /// <param name="workspace">工作区定义。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<TaskScanResult> ScanAsync(WorkspaceDefinition workspace, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var accepted = new List<(string SourcePath, TaskDocument Document)>();
        var problems = new List<string>();
        var scanned = 0;

        if (Directory.Exists(workspace.TaskDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(workspace.TaskDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                scanned++;
                if (TaskFileReader.TryRead(path, out var document, out var error) && document is not null)
                {
                    accepted.Add((path, document));
                }
                else
                {
                    problems.Add($"{Path.GetFileName(path)}：{error}");
                }
            }
        }

        using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        foreach (var (sourcePath, document) in accepted)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO task_view
                  (workspace_id, id, source_path, content_hash, title, status, priority, write_scope, payload, seen_at, updated_at)
                VALUES
                  ($workspace, $id, $sourcePath, $hash, $title, $status, $priority, $scope, $payload, $now, $now)
                ON CONFLICT (workspace_id, id) DO UPDATE SET
                  source_path  = excluded.source_path,
                  content_hash = excluded.content_hash,
                  title        = excluded.title,
                  status       = excluded.status,
                  priority     = excluded.priority,
                  write_scope  = excluded.write_scope,
                  payload      = excluded.payload,
                  seen_at      = excluded.seen_at,
                  updated_at   = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$workspace", workspace.Id);
            command.Parameters.AddWithValue("$id", document.Id);
            command.Parameters.AddWithValue("$sourcePath", sourcePath);
            command.Parameters.AddWithValue("$hash", document.ContentHash);
            command.Parameters.AddWithValue("$title", document.Title);
            command.Parameters.AddWithValue("$status", document.Status);
            command.Parameters.AddWithValue("$priority", document.Priority);
            command.Parameters.AddWithValue("$scope", System.Text.Json.JsonSerializer.Serialize(document.WriteScope));
            command.Parameters.AddWithValue("$payload", document.RawJson);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var markedMissing = await MarkMissingAsync(connection, transaction, workspace.Id, accepted, now, cancellationToken)
            .ConfigureAwait(false);

        transaction.Commit();

        return new TaskScanResult(scanned, accepted.Count, scanned - accepted.Count, markedMissing, problems);
    }

    /// <summary>
    /// 把本次未出现的任务标记为 missing（已是 missing 的不重复计数）。
    /// 本次出现的 id 先写入连接级临时表再取差集：避免逐个 <c>AND id &lt;&gt; ?</c> 拼接，
    /// 1000 个任务时会触发 SQLite "Expression tree is too large (maximum depth 1000)"。
    /// </summary>
    /// <param name="connection">连接。</param>
    /// <param name="transaction">当前事务。</param>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="accepted">本次出现的任务。</param>
    /// <param name="now">时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<int> MarkMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        IReadOnlyList<(string SourcePath, TaskDocument Document)> accepted,
        string now,
        CancellationToken cancellationToken)
    {
        using (var prepare = connection.CreateCommand())
        {
            prepare.Transaction = transaction;
            prepare.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS seen_task_id (id TEXT PRIMARY KEY);
                DELETE FROM seen_task_id;
                """;
            await prepare.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO seen_task_id (id) VALUES ($id);";
            var idParameter = insert.Parameters.Add("$id", SqliteType.Text);

            foreach (var (_, document) in accepted)
            {
                idParameter.Value = document.Id;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE task_view
            SET status = 'missing', updated_at = $now
            WHERE workspace_id = $workspace
              AND status <> 'missing'
              AND id NOT IN (SELECT id FROM seen_task_id);
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$workspace", workspaceId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
