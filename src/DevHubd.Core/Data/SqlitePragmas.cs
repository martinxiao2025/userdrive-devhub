using System.Data.Common;

namespace DevHubd.Core.Data;

/// <summary>
/// SQLite 连接级 PRAGMA 的唯一实现：裸连接（<see cref="SqliteConnectionFactory"/>）
/// 与 EF Core 连接（<see cref="SqlitePragmaInterceptor"/>）共用同一段脚本，避免两套配置漂移。
/// WAL 提升并发读写；busy_timeout=5000 让写锁竞争等待而不是立刻 SQLITE_BUSY；
/// foreign_keys=ON 保持外键校验；synchronous=NORMAL 与 WAL 搭配是官方推荐的低延迟组合。
/// </summary>
internal static class SqlitePragmas
{
    /// <summary>连接建立后必须执行的 PRAGMA 脚本。</summary>
    internal const string Script =
        "PRAGMA journal_mode=WAL;" +
        "PRAGMA busy_timeout=5000;" +
        "PRAGMA foreign_keys=ON;" +
        "PRAGMA synchronous=NORMAL;";

    /// <summary>同步执行 PRAGMA。</summary>
    /// <param name="connection">已打开的连接。</param>
    internal static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Script;
        command.ExecuteNonQuery();
    }

    /// <summary>异步执行 PRAGMA。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal static async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Script;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
