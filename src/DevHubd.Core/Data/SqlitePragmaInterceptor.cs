using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DevHubd.Core.Data;

/// <summary>
/// EF Core 连接拦截器：每次连接打开后执行统一的 SQLite PRAGMA，
/// 让 EF 连接与 <see cref="SqliteConnectionFactory"/> 打开的裸连接行为完全一致。
/// PRAGMA 是连接级状态，因此不能只在上下文创建时执行一次。
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    /// <summary>同步连接打开后应用 PRAGMA。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="eventData">事件数据。</param>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SqlitePragmas.Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    /// <summary>异步连接打开后应用 PRAGMA。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="eventData">事件数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成的任务。</returns>
    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        SqlitePragmas.Apply(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }
}
