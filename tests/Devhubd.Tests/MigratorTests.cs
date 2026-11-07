using Devhubd.Core.Data;
using Xunit;

namespace Devhubd.Tests;

/// <summary>
/// 迁移骨架测试：版本、9 张表、关键索引、单例行与幂等性。
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
    public async Task Migrate_CreatesAllExpectedTables()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));

        var version = await Migrator.MigrateAsync(factory);
        Assert.Equal(Migrator.CurrentVersion, version);

        using var connection = await factory.OpenAsync();
        Assert.Equal(Migrator.CurrentVersion, await Migrator.GetUserVersionAsync(connection));

        var tables = await Migrator.GetTableNamesAsync(connection);
        foreach (var expected in Migrator.ExpectedTables)
        {
            Assert.Contains(expected, tables);
        }
    }

    /// <summary>重复迁移幂等，且不重复插入单例行。</summary>
    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));

        await Migrator.MigrateAsync(factory);
        var second = await Migrator.MigrateAsync(factory);
        Assert.Equal(Migrator.CurrentVersion, second);

        using var connection = await factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM scheduler_state WHERE id = 1;";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    /// <summary>关键索引必须存在，否则 P1 的队列查询会退化为全表扫描。</summary>
    [Fact]
    public async Task Migrate_CreatesQueueAndResultIndexes()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(factory);

        using var connection = await factory.OpenAsync();
        var indexes = await Migrator.GetIndexNamesAsync(connection);

        Assert.Contains("idx_queue_ready", indexes);
        Assert.Contains("idx_queue_task", indexes);
        Assert.Contains("idx_result_unread", indexes);
        Assert.Contains("idx_scope_expiry", indexes);
        Assert.Contains("idx_audit_at", indexes);
    }

    /// <summary>P0 必须落成 dispatch_only 默认档位。</summary>
    [Fact]
    public async Task Migrate_SeedsDispatchOnlyMode()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(factory);

        using var connection = await factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT mode, enabled FROM scheduler_state WHERE id = 1;";
        using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("dispatch_only", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    /// <summary>数据库启用 WAL 且外键约束打开。</summary>
    [Fact]
    public async Task Open_AppliesWalAndForeignKeys()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        await Migrator.MigrateAsync(factory);

        using var connection = await factory.OpenAsync();

        using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(await journal.ExecuteScalarAsync())?.ToLowerInvariant());

        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, Convert.ToInt64(await foreignKeys.ExecuteScalarAsync()));
    }
}
