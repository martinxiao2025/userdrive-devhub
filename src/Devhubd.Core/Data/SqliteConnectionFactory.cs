using Microsoft.Data.Sqlite;

namespace Devhubd.Core.Data;

/// <summary>
/// SQLite 连接工厂：统一连接串与每次开连接时必需的 PRAGMA（WAL / busy_timeout / foreign_keys）。
/// 连接只在使用范围内持有，不跨 await 长期占用。
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

    /// <summary>
    /// 打开连接并应用基础 PRAGMA。调用方负责释放连接。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA busy_timeout=5000;" +
            "PRAGMA foreign_keys=ON;" +
            "PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }
}
