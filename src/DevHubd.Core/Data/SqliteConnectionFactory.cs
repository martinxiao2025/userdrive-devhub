using Microsoft.Data.Sqlite;

namespace DevHubd.Core.Data;

/// <summary>
/// SQLite 连接工厂：统一连接串与每次开连接时必需的 PRAGMA（WAL / busy_timeout / foreign_keys）。
/// 连接只在使用范围内持有，不跨 await 长期占用。
/// 迁移（<see cref="Migrator"/>）、任务扫描等需要裸连接的场景仍使用本类；
/// EF Core 侧通过 <see cref="ConnectionString"/> 复用同一条连接串，PRAGMA 由
/// <see cref="SqlitePragmas"/> 统一提供，不存在两套配置。
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// 用数据库文件路径构造工厂。
    /// </summary>
    /// <param name="databasePath">SQLite 文件绝对路径。</param>
    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    /// <summary>连接串（供 EF Core 的 <c>UseSqlite</c> 复用，保证两种访问指向同一个库）。</summary>
    public string ConnectionString => _connectionString;

    /// <summary>
    /// 打开连接并应用基础 PRAGMA。调用方负责释放连接。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlitePragmas.ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
