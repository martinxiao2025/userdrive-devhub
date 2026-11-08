using DevHubd.Core.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 迁移骨架测试：版本、期望表、关键索引、单例行与幂等性。
/// xUnit v3 起所有异步调用传入 <see cref="TestContext.Current"/> 的取消令牌，以便测试超时能被及时打断。
/// </summary>
public sealed class MigratorTests : IDisposable
{
    private readonly string _directory;

    /// <summary>为每个测试创建独立临时数据目录。</summary>
    public MigratorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    /// <summary>清理临时目录。SQLite 连接池可能短暂占用文件，清理失败不视为测试失败。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }
    }

    /// <summary>迁移后版本为最新，且期望表全部存在。</summary>
    [Fact]
    public async Task Migrate_CreatesAllExpectedTablesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));

        var version = await Migrator.MigrateAsync(factory, ct);
        Assert.Equal(Migrator.CurrentVersion, version);

        using var connection = await factory.OpenAsync(ct);
        Assert.Equal(Migrator.CurrentVersion, await Migrator.GetUserVersionAsync(connection, ct));

        var tables = await Migrator.GetTableNamesAsync(connection, ct);
        foreach (var expected in Migrator.ExpectedTables)
        {
            Assert.Contains(expected, tables);
        }
    }

    /// <summary>重复迁移幂等，且不重复插入单例行。</summary>
    [Fact]
    public async Task Migrate_IsIdempotentAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));

        await Migrator.MigrateAsync(factory, ct);
        var second = await Migrator.MigrateAsync(factory, ct);
        Assert.Equal(Migrator.CurrentVersion, second);

        using var connection = await factory.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM scheduler_state WHERE id = 1;";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(ct)));
    }

    /// <summary>关键索引必须存在，否则 P1 的队列查询会退化为全表扫描。</summary>
    [Fact]
    public async Task Migrate_CreatesQueueAndResultIndexesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(factory, ct);

        using var connection = await factory.OpenAsync(ct);
        var indexes = await Migrator.GetIndexNamesAsync(connection, ct);

        Assert.Contains("idx_queue_ready", indexes);
        Assert.Contains("idx_queue_task", indexes);
        Assert.Contains("idx_result_unread", indexes);
        Assert.Contains("idx_audit_at", indexes);
    }

    /// <summary>P0 必须落成 dispatch_only 默认档位。</summary>
    [Fact]
    public async Task Migrate_SeedsDispatchOnlyModeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(factory, ct);

        using var connection = await factory.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT mode, enabled FROM scheduler_state WHERE id = 1;";
        using var reader = await command.ExecuteReaderAsync(ct);

        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("dispatch_only", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    /// <summary>数据库启用 WAL 且外键约束打开。</summary>
    [Fact]
    public async Task Open_AppliesWalAndForeignKeysAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(factory, ct);

        using var connection = await factory.OpenAsync(ct);

        using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(await journal.ExecuteScalarAsync(ct))?.ToLowerInvariant());

        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, Convert.ToInt64(await foreignKeys.ExecuteScalarAsync(ct)));
    }

    /// <summary>V2 必须给 attempt 增加 usage_estimated，且默认值为 0（真实 token）。</summary>
    [Fact]
    public async Task Migrate_AddsUsageEstimatedColumnAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(factory, ct);

        Assert.True(await HasColumnAsync(factory, "attempt", "usage_estimated", ct));
    }

    /// <summary>V3 必须给 attempt 增加 usage_available 与 rounds_exhausted，旧行默认 0。</summary>
    [Fact]
    public async Task Migrate_AddsUsageAvailableAndRoundsExhaustedColumnsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(factory, ct);

        Assert.True(await HasColumnAsync(factory, "attempt", "usage_available", ct));
        Assert.True(await HasColumnAsync(factory, "attempt", "rounds_exhausted", ct));
    }

    /// <summary>
    /// V4 必须回填“已有真实 token、但 v3 默认写成 0”的旧行：让 <c>usage_available</c> 与
    /// 契约「false ⇔ token 为 NULL」一致；token 为 NULL 的行保持 0。
    /// 这里直接构造 v3 形态的库（而不是先迁到最新再回退版本号），避免 V5 已加列后重复执行。
    /// </summary>
    [Fact]
    public async Task Migrate_V4_BackfillsUsageAvailableForLegacyTokenRowsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));

        using (var connection = await factory.OpenAsync(ct))
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                CREATE TABLE queue_item (id TEXT PRIMARY KEY);
                CREATE TABLE task_view (id TEXT);
                CREATE TABLE attempt (
                    id TEXT PRIMARY KEY,
                    queue_item_id TEXT NOT NULL,
                    input_tokens INTEGER,
                    output_tokens INTEGER,
                    usage_estimated INTEGER NOT NULL DEFAULT 0,
                    usage_available INTEGER NOT NULL DEFAULT 0,
                    rounds_exhausted INTEGER NOT NULL DEFAULT 0);
                INSERT INTO attempt (id, queue_item_id, input_tokens, output_tokens)
                VALUES ('a1', 'q1', 100, 20);
                INSERT INTO attempt (id, queue_item_id, input_tokens, output_tokens)
                VALUES ('a2', 'q1', NULL, NULL);
                PRAGMA user_version = 3;
                """;
            await seed.ExecuteNonQueryAsync(ct);
        }

        Assert.Equal(Migrator.CurrentVersion, await Migrator.MigrateAsync(factory, ct));

        using var verify = await factory.OpenAsync(ct);
        using var command = verify.CreateCommand();
        command.CommandText = "SELECT id, usage_available FROM attempt ORDER BY id;";
        using var reader = await command.ExecuteReaderAsync(ct);

        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("a1", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));

        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("a2", reader.GetString(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    /// <summary>
    /// V5 只加列/加表：任务角色/依赖/审批、队列挂起、问题单与角色快照必须存在，且旧行默认值不改变行为。
    /// </summary>
    [Fact]
    public async Task Migrate_V5_AddsRoleAndInputRequestStructuresAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(factory, ct);

        Assert.True(await HasColumnAsync(factory, "task_view", "role_id", ct));
        Assert.True(await HasColumnAsync(factory, "task_view", "depends_on", ct));
        Assert.True(await HasColumnAsync(factory, "task_view", "approval_state", ct));
        Assert.True(await HasColumnAsync(factory, "queue_item", "role_id", ct));
        Assert.True(await HasColumnAsync(factory, "queue_item", "hold_kind", ct));
        Assert.True(await HasColumnAsync(factory, "queue_item", "hold_ref", ct));

        using var connection = await factory.OpenAsync(ct);
        var tables = await Migrator.GetTableNamesAsync(connection, ct);
        Assert.Contains("role_view", tables);
        Assert.Contains("input_request", tables);

        var indexes = await Migrator.GetIndexNamesAsync(connection, ct);
        Assert.Contains("idx_queue_role", indexes);
        Assert.Contains("idx_input_dedupe_open", indexes);
        Assert.Contains("idx_input_open", indexes);
        Assert.Contains("idx_input_task", indexes);

        using var defaults = connection.CreateCommand();
        defaults.CommandText = """
            INSERT INTO task_view (workspace_id, id, source_path, content_hash, title, status, priority, write_scope, payload, seen_at, updated_at)
            VALUES ('ws', 'T1', 'p', 'h', 't', 'ready', 0, '', '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            SELECT role_id, approval_state FROM task_view WHERE id = 'T1';
            """;
        using var reader = await defaults.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("implementer", reader.GetString(0));
        Assert.Equal("none", reader.GetString(1));
    }

    /// <summary>
    /// V7 必须把"旧 v6 把 rerun_pending 建成了 INTEGER"的完整库重建为 TEXT，并把历史布尔值归一化为空串：
    /// 历史值 <c>1</c> 无法还原对应内容哈希，保留会被误读成哈希导致审批后多跑一轮。
    /// 同时验证行数、其它列与外键完整性不受重建影响，未使用的 scope_lock 被删除。
    /// </summary>
    [Fact]
    public async Task Migrate_V7_RebuildsLegacyV6RerunPendingAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));

        using (var connection = await factory.OpenAsync(ct))
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                CREATE TABLE task_view (
                  workspace_id   TEXT NOT NULL,
                  id             TEXT NOT NULL,
                  source_path    TEXT NOT NULL,
                  content_hash   TEXT NOT NULL,
                  title          TEXT NOT NULL,
                  status         TEXT NOT NULL CHECK (status IN ('ready','blocked','done','cancelled','missing')),
                  priority       INTEGER NOT NULL,
                  write_scope    TEXT NOT NULL,
                  payload        TEXT NOT NULL,
                  seen_at        TEXT NOT NULL,
                  updated_at     TEXT NOT NULL,
                  role_id        TEXT NOT NULL DEFAULT 'implementer',
                  depends_on     TEXT,
                  approval_state TEXT NOT NULL DEFAULT 'none'
                    CHECK (approval_state IN ('none','pending','approved','rejected')),
                  rerun_pending  INTEGER NOT NULL DEFAULT 0,
                  PRIMARY KEY (workspace_id, id)
                );
                CREATE INDEX idx_task_view_status ON task_view(workspace_id, status);
                CREATE TABLE scope_lock (
                  workspace_id TEXT NOT NULL,
                  scope        TEXT NOT NULL,
                  owner_id     TEXT NOT NULL,
                  acquired_at  TEXT NOT NULL,
                  expires_at   TEXT NOT NULL,
                  PRIMARY KEY (workspace_id, scope)
                );
                INSERT INTO task_view
                  (workspace_id, id, source_path, content_hash, title, status, priority, write_scope,
                   payload, seen_at, updated_at, role_id, depends_on, approval_state, rerun_pending)
                VALUES
                  ('alpha', 'T-001', '.devhub/tasks/T-001.json', 'h1', '待审任务', 'ready', 10, '["src"]',
                   '{"id":"T-001"}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'implementer', '["T-000"]', 'pending', 1),
                  ('alpha', 'T-002', '.devhub/tasks/T-002.json', 'h2', '已交付任务', 'done', 5, '[]',
                   '{"id":"T-002"}', '2026-01-02T00:00:00Z', '2026-01-02T00:00:00Z', 'reviewer', NULL, 'approved', 0);
                PRAGMA user_version = 6;
                """;
            await seed.ExecuteNonQueryAsync(ct);
        }

        Assert.Equal(Migrator.CurrentVersion, await Migrator.MigrateAsync(factory, ct));

        using var verify = await factory.OpenAsync(ct);
        using (var type = verify.CreateCommand())
        {
            type.CommandText = "SELECT type FROM pragma_table_info('task_view') WHERE name = 'rerun_pending';";
            Assert.Equal("TEXT", Convert.ToString(await type.ExecuteScalarAsync(ct)));
        }

        using (var rows = verify.CreateCommand())
        {
            rows.CommandText = "SELECT id, status, approval_state, role_id, depends_on, rerun_pending FROM task_view ORDER BY id;";
            using var reader = await rows.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal("T-001", reader.GetString(0));
            Assert.Equal("ready", reader.GetString(1));
            Assert.Equal("pending", reader.GetString(2));
            Assert.Equal("implementer", reader.GetString(3));
            Assert.Equal("[\"T-000\"]", reader.GetString(4));
            Assert.Equal(string.Empty, reader.GetString(5));

            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal("T-002", reader.GetString(0));
            Assert.Equal("done", reader.GetString(1));
            Assert.Equal("approved", reader.GetString(2));
            Assert.Equal("reviewer", reader.GetString(3));
            Assert.True(reader.IsDBNull(4));
            Assert.Equal(string.Empty, reader.GetString(5));
        }

        Assert.Equal(2L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM task_view;", ct));
        Assert.Equal("ok", Convert.ToString(await ScalarAsync(verify, "PRAGMA integrity_check;", ct)));
        Assert.Null(await ScalarAsync(verify, "PRAGMA foreign_key_check;", ct));

        var tables = await Migrator.GetTableNamesAsync(verify, ct);
        Assert.DoesNotContain("scope_lock", tables);
        Assert.Contains("task_view", tables);
    }

    /// <summary>
    /// V7 对极简自建库（无 source_path 等完整列）只做值归一化，不尝试重建表：
    /// 历史 <c>1</c> 归一化为空串，其它列结构保持不变。
    /// </summary>
    [Fact]
    public async Task Migrate_V7_NormalizesMinimalLegacyV6Async()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));

        using (var connection = await factory.OpenAsync(ct))
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                CREATE TABLE task_view (workspace_id TEXT, id TEXT, rerun_pending INTEGER NOT NULL DEFAULT 0);
                INSERT INTO task_view (workspace_id, id, rerun_pending) VALUES ('ws', 'T1', 1);
                INSERT INTO task_view (workspace_id, id, rerun_pending) VALUES ('ws', 'T2', 0);
                PRAGMA user_version = 6;
                """;
            await seed.ExecuteNonQueryAsync(ct);
        }

        Assert.Equal(Migrator.CurrentVersion, await Migrator.MigrateAsync(factory, ct));

        using var verify = await factory.OpenAsync(ct);
        using var command = verify.CreateCommand();
        command.CommandText = "SELECT DISTINCT rerun_pending FROM task_view;";
        Assert.Equal(string.Empty, Convert.ToString(await command.ExecuteScalarAsync(ct)));
    }

    /// <summary>已有 V1 库升级时必须逐级补 V2/V3/V4，不重建表，并保持 user_version 递增。</summary>
    [Fact]
    public async Task Migrate_FromV1_UpgradesToLatestAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));

        using (var connection = await factory.OpenAsync(ct))
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                CREATE TABLE attempt (id TEXT PRIMARY KEY, queue_item_id TEXT NOT NULL);
                CREATE TABLE task_view (id TEXT);
                CREATE TABLE queue_item (id TEXT);
                PRAGMA user_version = 1;
                """;
            await seed.ExecuteNonQueryAsync(ct);
        }

        var version = await Migrator.MigrateAsync(factory, ct);

        Assert.Equal(Migrator.CurrentVersion, version);
        Assert.True(await HasColumnAsync(factory, "attempt", "usage_estimated", ct));
        Assert.True(await HasColumnAsync(factory, "attempt", "usage_available", ct));
        Assert.True(await HasColumnAsync(factory, "attempt", "rounds_exhausted", ct));
    }

    /// <summary>在已打开的连接上执行标量查询。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">查询。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>在已打开的连接上执行返回计数的查询。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">查询。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
        => Convert.ToInt64(await ScalarAsync(connection, sql, cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>判断指定表是否已有某列。</summary>
    /// <param name="factory">连接工厂。</param>
    /// <param name="table">表名。</param>
    /// <param name="column">列名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<bool> HasColumnAsync(
        SqliteConnectionFactory factory,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        using var connection = await factory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
