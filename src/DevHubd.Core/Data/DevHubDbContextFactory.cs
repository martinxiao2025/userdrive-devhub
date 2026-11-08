using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Data;

/// <summary>
/// 基于连接串的 <see cref="IDbContextFactory{TContext}"/> 实现。
/// 生产装配走 <c>AddDbContextFactory</c>；本类用于测试等"已有
/// <see cref="SqliteConnectionFactory"/>、但没有 DI 容器"的场景，
/// 让 store 的公开构造签名保持不变。
/// </summary>
public sealed class DevHubDbContextFactory : IDbContextFactory<DevHubDbContext>
{
    private readonly DbContextOptions<DevHubDbContext> _options;

    /// <summary>用数据库连接串构造工厂。</summary>
    /// <param name="connectionString">SQLite 连接串。</param>
    public DevHubDbContextFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _options = new DbContextOptionsBuilder<DevHubDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
    }

    /// <summary>创建一个新的上下文实例（用完即弃，不跨方法共享）。</summary>
    /// <returns>新的 <see cref="DevHubDbContext"/>。</returns>
    public DevHubDbContext CreateDbContext() => new(_options);

    /// <summary>异步创建上下文（SQLite 无异步打开开销，直接复用同步路径）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新的 <see cref="DevHubDbContext"/>。</returns>
    public Task<DevHubDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
