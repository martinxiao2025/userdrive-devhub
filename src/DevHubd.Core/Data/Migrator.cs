using Microsoft.Data.Sqlite;

namespace DevHubd.Core.Data;

/// <summary>
/// 数据库迁移器：以 <c>PRAGMA user_version</c> 记录版本，按版本号顺序执行增量迁移（V1 → V2 …）。
/// 迁移在单个事务内完成，失败整体回滚；重复调用幂等。
/// </summary>
public static class Migrator
{
    /// <summary>当前 schema 版本。V1 = 9 张表；V2 = attempt.usage_estimated。</summary>
    public const int CurrentVersion = 2;

    /// <summary>V1 的全部 DDL 与初始数据。</summary>
    private const string SchemaV1 = """
        CREATE TABLE task_view (
          workspace_id  TEXT NOT NULL,
          id            TEXT NOT NULL,
          source_path   TEXT NOT NULL,
          content_hash  TEXT NOT NULL,
          title         TEXT NOT NULL,
          status        TEXT NOT NULL CHECK (status IN ('ready','blocked','done','cancelled','missing')),
          priority      INTEGER NOT NULL,
          write_scope   TEXT NOT NULL,
          payload       TEXT NOT NULL,
          seen_at       TEXT NOT NULL,
          updated_at    TEXT NOT NULL,
          PRIMARY KEY (workspace_id, id)
        );
        CREATE INDEX idx_task_view_status ON task_view(workspace_id, status);

        CREATE TABLE queue_item (
          id                 TEXT PRIMARY KEY,
          workspace_id       TEXT NOT NULL,
          task_id            TEXT NOT NULL,
          task_hash          TEXT NOT NULL,
          dedupe_key         TEXT NOT NULL UNIQUE,
          status             TEXT NOT NULL CHECK (status IN
                               ('pending','claimed','running','succeeded','failed','dead','cancelled')),
          priority           INTEGER NOT NULL,
          attempt            INTEGER NOT NULL DEFAULT 0,
          max_attempts       INTEGER NOT NULL DEFAULT 3,
          preferred_provider TEXT NOT NULL DEFAULT 'openai',
          available_at       TEXT NOT NULL,
          lease_owner        TEXT,
          lease_expires_at   TEXT,
          last_error_code    TEXT,
          last_error         TEXT,
          created_at         TEXT NOT NULL,
          updated_at         TEXT NOT NULL
        );
        CREATE INDEX idx_queue_ready ON queue_item(workspace_id, status, priority, available_at);
        CREATE INDEX idx_queue_task  ON queue_item(workspace_id, task_id, status);

        CREATE TABLE attempt (
          id            TEXT PRIMARY KEY,
          queue_item_id TEXT NOT NULL REFERENCES queue_item(id),
          attempt_no    INTEGER NOT NULL,
          provider      TEXT NOT NULL,
          model         TEXT,
          started_at    TEXT NOT NULL,
          ended_at      TEXT,
          outcome       TEXT CHECK (outcome IN ('succeeded','failed','abandoned')),
          error_kind    TEXT,
          error_code    TEXT,
          tool_calls    INTEGER,
          input_tokens  INTEGER,
          output_tokens INTEGER,
          files_changed INTEGER,
          summary       TEXT,
          UNIQUE (queue_item_id, attempt_no)
        );

        CREATE TABLE result (
          id            TEXT PRIMARY KEY,
          task_id       TEXT NOT NULL,
          queue_item_id TEXT NOT NULL REFERENCES queue_item(id),
          provider      TEXT NOT NULL,
          outcome       TEXT NOT NULL CHECK (outcome IN ('delivered','awaiting_review','failed')),
          summary       TEXT NOT NULL,
          artifacts     TEXT,
          created_at    TEXT NOT NULL,
          read_at       TEXT
        );
        CREATE INDEX idx_result_unread ON result(read_at, created_at);

        CREATE TABLE notice (
          seq        INTEGER PRIMARY KEY AUTOINCREMENT,
          dedupe_key TEXT NOT NULL UNIQUE,
          kind       TEXT NOT NULL CHECK (kind IN ('result','failure','breaker','auth','provider')),
          task_id    TEXT,
          title      TEXT NOT NULL,
          body       TEXT,
          created_at TEXT NOT NULL,
          acked_at   TEXT
        );

        CREATE TABLE provider_state (
          provider             TEXT PRIMARY KEY,
          state                TEXT NOT NULL CHECK (state IN ('ready','down','no_model','quota','auth')),
          detail               TEXT,
          breaker              TEXT NOT NULL CHECK (breaker IN ('closed','open','half_open')),
          breaker_reason       TEXT,
          breaker_opened_at    TEXT,
          consecutive_failures INTEGER NOT NULL DEFAULT 0,
          checked_at           TEXT NOT NULL
        );

        CREATE TABLE scheduler_state (
          id               INTEGER PRIMARY KEY CHECK (id = 1),
          enabled          INTEGER NOT NULL,
          mode             TEXT NOT NULL CHECK (mode IN ('auto','dispatch_only')),
          pause_reason     TEXT,
          interval_seconds INTEGER NOT NULL,
          last_scan_at     TEXT,
          next_scan_at     TEXT,
          counters         TEXT NOT NULL,
          updated_at       TEXT NOT NULL
        );

        CREATE TABLE scope_lock (
          workspace_id TEXT NOT NULL,
          scope        TEXT NOT NULL,
          owner_id     TEXT NOT NULL,
          acquired_at  TEXT NOT NULL,
          expires_at   TEXT NOT NULL,
          PRIMARY KEY (workspace_id, scope)
        );
        CREATE INDEX idx_scope_expiry ON scope_lock(expires_at);

        CREATE TABLE audit (
          id       INTEGER PRIMARY KEY AUTOINCREMENT,
          at       TEXT NOT NULL,
          actor    TEXT NOT NULL,
          action   TEXT NOT NULL,
          subject  TEXT,
          provider TEXT,
          detail   TEXT,
          trace_id TEXT
        );
        CREATE INDEX idx_audit_at ON audit(at);

        INSERT INTO scheduler_state (id, enabled, mode, pause_reason, interval_seconds, counters, updated_at)
        VALUES (1, 1, 'dispatch_only', NULL, 30, '{}', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
        """;

    /// <summary>
    /// V2：attempt 增加 <c>usage_estimated</c>。0 表示 provider 真实返回的 token；
    /// 1 表示按请求/响应字符数推算的近似值，<b>不是真实 token</b>。
    /// </summary>
    private const string SchemaV2 = """
        ALTER TABLE attempt ADD COLUMN usage_estimated INTEGER NOT NULL DEFAULT 0;
        """;

    /// <summary>P0 期望存在的 9 张表（用于启动自检与测试断言）。</summary>
    public static readonly IReadOnlyList<string> ExpectedTables = new[]
    {
        "task_view",
        "queue_item",
        "attempt",
        "result",
        "notice",
        "provider_state",
        "scheduler_state",
        "scope_lock",
        "audit",
    };

    /// <summary>
    /// 迁移到最新版本；已是最新则直接返回当前版本。
    /// 按 <c>user_version</c> 顺序补执行缺失的增量脚本，已有的库只跑新增的那一段。
    /// </summary>
    /// <param name="factory">连接工厂。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>迁移后的 schema 版本。</returns>
    public static async Task<int> MigrateAsync(SqliteConnectionFactory factory, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var version = await GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (version >= CurrentVersion)
        {
            return version;
        }

        using var transaction = connection.BeginTransaction();

        if (version < 1)
        {
            await ExecuteScriptAsync(connection, transaction, SchemaV1, cancellationToken).ConfigureAwait(false);
            version = 1;
        }

        if (version < 2)
        {
            await ExecuteScriptAsync(connection, transaction, SchemaV2, cancellationToken).ConfigureAwait(false);
            version = 2;
        }

        using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = transaction;
            // PRAGMA 不支持参数化，这里的值来自编译期常量。
            stamp.CommandText = $"PRAGMA user_version = {CurrentVersion};";
            await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return CurrentVersion;
    }

    /// <summary>在迁移事务内执行一段 DDL。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="transaction">迁移事务。</param>
    /// <param name="script">DDL 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task ExecuteScriptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string script,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = script;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取当前 schema 版本。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>列出当前库中的用户表（不含 sqlite_ 内部表），按名字排序。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<IReadOnlyList<string>> GetTableNamesAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        var names = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>列出当前库中的索引名（不含自动索引），按名字排序。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<IReadOnlyList<string>> GetIndexNamesAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        var names = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
