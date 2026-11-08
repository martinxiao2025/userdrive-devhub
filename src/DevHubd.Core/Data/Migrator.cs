using Microsoft.Data.Sqlite;

namespace DevHubd.Core.Data;

/// <summary>
/// 数据库迁移器：以 <c>PRAGMA user_version</c> 记录版本，按版本号顺序执行增量迁移（V1 → V2 …）。
/// 迁移在单个事务内完成，失败整体回滚；重复调用幂等。
/// <para>
/// 分工：本类是 9 张表 DDL/索引/版本推进的<b>唯一</b>管理者；EF Core
/// （<see cref="DevHubDbContext"/>）只负责映射与读写，不建表、不新增
/// <c>__EFMigrationsHistory</c>，也不允许另起一套迁移。修改 schema 必须在这里加版本。
/// </para>
/// </summary>
public static class Migrator
{
    /// <summary>
    /// 当前 schema 版本。V1 = 9 张表；V2 = attempt.usage_estimated；V3 = attempt.usage_available / rounds_exhausted；
    /// V4 = 旧行 usage_available 回填；V5 = 角色快照、任务角色/依赖/审批列、队列挂起列与问题单表；
    /// V6 = queue_item.forced（force 派发标记）与 task_view.rerun_pending（待审期间的再执行需求）。
    /// </summary>
    public const int CurrentVersion = 6;

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

    /// <summary>
    /// V3：attempt 增加 <c>usage_available</c> 与 <c>rounds_exhausted</c>。
    /// <para>
    /// <c>usage_available</c>：1 表示 provider 明确返回了用量；0 表示用量不可得（token 列为 NULL）。
    /// 旧行先统一默认 0，再由 V4 把“已有真实 token”的行回填为 1。
    /// </para>
    /// <para>
    /// <c>rounds_exhausted</c>：1 表示回合耗尽工具轮次预算，最终文本是框架移除工具后的兜底答复，
    /// 与“模型自然完成”不是同一语义。旧行默认 0。
    /// </para>
    /// </summary>
    private const string SchemaV3 = """
        ALTER TABLE attempt ADD COLUMN usage_available INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE attempt ADD COLUMN rounds_exhausted INTEGER NOT NULL DEFAULT 0;
        """;

    /// <summary>
    /// V4：回填 v3 的旧行 <c>usage_available</c>。v3 统一写 0，但 v2 及更早的 BYOK 行可能已经记下真实 token，
    /// 此时 0 会被误读成“provider 没返回用量”。凡 token 列非空的旧行补标为 1，
    /// 与契约「usageAvailable=false ⇔ token 为 NULL」保持一致。只更新仍为 0 的行，幂等。
    /// </summary>
    private const string SchemaV4 = """
        UPDATE attempt
           SET usage_available = 1
         WHERE usage_available = 0
           AND (input_tokens IS NOT NULL OR output_tokens IS NOT NULL);
        """;

    /// <summary>
    /// V5：角色调度与不确定项回流的持久化结构。<b>只加列/加表，不重建表、不重写数据</b>。
    /// <para>
    /// 选择一次性写全 V5（而不是按单元递增为 V5/V6…）的原因：<see cref="Migrator"/> 按
    /// <c>user_version</c> 只执行一次，同一版本号无法分批追加；分版本会让每个单元都要改动版本常量。
    /// </para>
    /// <para>
    /// 各列含义：<c>task_view.role_id</c> 缺省 <c>implementer</c>；<c>queue_item.role_id/hold_*</c> 可空；
    /// <c>role_view</c> 是角色声明的数据库快照（状态必须在持久化层，见架构增量方案 §5）；
    /// <c>input_request</c> 是后台回合提出的问题单，<c>dedupe_key</c> 用<b>部分唯一索引</b>
    /// 只约束 <c>status='open'</c>：历史 <c>expired/cancelled</c> 记录不阻塞后续同题重问。
    /// </para>
    /// </summary>
    private const string SchemaV5 = """
        ALTER TABLE task_view ADD COLUMN role_id TEXT NOT NULL DEFAULT 'implementer';
        ALTER TABLE task_view ADD COLUMN depends_on TEXT;
        ALTER TABLE task_view ADD COLUMN approval_state TEXT NOT NULL DEFAULT 'none'
          CHECK (approval_state IN ('none','pending','approved','rejected'));

        ALTER TABLE queue_item ADD COLUMN role_id TEXT;
        ALTER TABLE queue_item ADD COLUMN hold_kind TEXT CHECK (hold_kind IN ('input','approval'));
        ALTER TABLE queue_item ADD COLUMN hold_ref TEXT;
        CREATE INDEX idx_queue_role ON queue_item(role_id, status);

        CREATE TABLE role_view (
          id                   TEXT PRIMARY KEY,
          name                 TEXT NOT NULL,
          description          TEXT NOT NULL,
          source_path          TEXT NOT NULL,
          content_hash         TEXT NOT NULL,
          skills               TEXT NOT NULL,
          tools                TEXT NOT NULL,
          delivery             TEXT NOT NULL CHECK (delivery IN ('auto','dispatch_only')),
          requires_approval    INTEGER NOT NULL,
          can_request_input    INTEGER NOT NULL,
          max_parallel         INTEGER NOT NULL,
          max_attempts         INTEGER NOT NULL,
          max_rounds           INTEGER NOT NULL,
          max_tool_calls       INTEGER NOT NULL,
          max_duration_seconds INTEGER NOT NULL,
          max_input_tokens     INTEGER,
          payload              TEXT NOT NULL,
          loaded_at            TEXT NOT NULL,
          updated_at           TEXT NOT NULL
        );

        CREATE TABLE input_request (
          id               TEXT PRIMARY KEY,
          workspace_id     TEXT NOT NULL,
          task_id          TEXT NOT NULL,
          queue_item_id    TEXT NOT NULL,
          role_id          TEXT NOT NULL,
          status           TEXT NOT NULL CHECK (status IN ('open','answered','expired','cancelled')),
          question         TEXT NOT NULL,
          context          TEXT,
          options          TEXT,
          missing_inputs   TEXT,
          allow_free_text  INTEGER NOT NULL DEFAULT 1,
          dedupe_key       TEXT NOT NULL,
          created_at       TEXT NOT NULL,
          expires_at       TEXT NOT NULL,
          answered_at      TEXT,
          answer           TEXT,
          answer_option_id TEXT,
          answer_source    TEXT CHECK (answer_source IN ('user','agent','elicitation')),
          answered_by      TEXT
        );
        CREATE UNIQUE INDEX idx_input_dedupe_open ON input_request(dedupe_key) WHERE status = 'open';
        CREATE INDEX idx_input_open ON input_request(status, created_at);
        CREATE INDEX idx_input_task ON input_request(workspace_id, task_id, status);
        """;

    /// <summary>
    /// V6：入队闸门与重做通道的持久化标记。<b>只加列</b>。
    /// <para>
    /// <c>queue_item.forced</c>：1 表示该队列项由显式 <c>force</c> 绕过"同任务已有活跃项或待审结果"闸门创建，
    /// 审计里同时有 <c>queue.dispatch_forced</c> 记录，便于追责与统计。
    /// </para>
    /// <para>
    /// <c>task_view.rerun_pending</c>：1 表示任务在待审期间发生了实质内容变化，
    /// 已登记"决策落地后还需再跑一轮"的需求；待审期间不会并行派发。
    /// </para>
    /// </summary>
    private const string SchemaV6 = """
        ALTER TABLE queue_item ADD COLUMN forced INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE task_view ADD COLUMN rerun_pending INTEGER NOT NULL DEFAULT 0;
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

        if (version < 3)
        {
            await ExecuteScriptAsync(connection, transaction, SchemaV3, cancellationToken).ConfigureAwait(false);
            version = 3;
        }

        if (version < 4)
        {
            // 兼容极简自建库：缺少 token 列时跳过回填，不阻断迁移。
            if (await HasColumnAsync(connection, transaction, "attempt", "input_tokens", cancellationToken).ConfigureAwait(false))
            {
                await ExecuteScriptAsync(connection, transaction, SchemaV4, cancellationToken).ConfigureAwait(false);
            }

            version = 4;
        }

        if (version < 5)
        {
            await ExecuteScriptAsync(connection, transaction, SchemaV5, cancellationToken).ConfigureAwait(false);
            version = 5;
        }

        if (version < 6)
        {
            await ExecuteScriptAsync(connection, transaction, SchemaV6, cancellationToken).ConfigureAwait(false);
            version = 6;
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

    /// <summary>判断指定表在迁移事务内是否已有某列（用于兼容极简自建库）。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="transaction">迁移事务。</param>
    /// <param name="table">表名。</param>
    /// <param name="column">列名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // PRAGMA 不支持参数化，表名来自编译期常量。
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
