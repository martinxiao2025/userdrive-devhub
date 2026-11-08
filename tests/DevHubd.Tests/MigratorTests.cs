using DevHubd.Core.Data;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 迁移骨架测试：版本、9 张表、关键索引、单例行与幂等性。
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

    /// <summary>迁移后版本为 1，且 9 张表全部存在。</summary>
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
        Assert.Contains("idx_scope_expiry", indexes);
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

    /// <summary>已有 V1 库升级时必须只补 V2，不重建表，并保持 user_version 递增。</summary>
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
                PRAGMA user_version = 1;
                """;
            await seed.ExecuteNonQueryAsync(ct);
        }

        var version = await Migrator.MigrateAsync(factory, ct);

        Assert.Equal(Migrator.CurrentVersion, version);
        Assert.True(await HasColumnAsync(factory, "attempt", "usage_estimated", ct));
    }

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
