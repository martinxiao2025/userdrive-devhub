using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using DevHubd.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Roles;

/// <summary>角色快照同步结果，用于启动日志。</summary>
/// <param name="Added">新增角色数。</param>
/// <param name="Updated">内容哈希变化而更新的角色数。</param>
/// <param name="Removed">声明文件中已删除、从快照移除的角色数。</param>
/// <param name="Unchanged">内容未变、无需写入的角色数。</param>
public sealed record RoleSnapshotSyncResult(int Added, int Updated, int Removed, int Unchanged);

/// <summary>
/// 角色快照存取：把已校验的角色声明写入 <c>role_view</c>，并供状态面以库为准读取。
/// 同步是幂等的：内容哈希未变不写库；声明文件中消失的角色会被删除。
/// </summary>
public sealed class RoleSnapshotStore
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public RoleSnapshotStore(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>把角色声明同步到 <c>role_view</c>（新增 / 更新 / 删除）。</summary>
    /// <param name="roles">已校验的角色声明。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>同步计数。</returns>
    public async Task<RoleSnapshotSyncResult> SyncAsync(
        IReadOnlyList<RoleDefinition> roles,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.RoleViews.ToDictionaryAsync(item => item.Id, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        var now = UtcTime.Now();
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var role in roles)
        {
            if (!existing.TryGetValue(role.Id, out var entity))
            {
                db.RoleViews.Add(CreateEntity(role, now));
                added++;
                continue;
            }

            if (string.Equals(entity.ContentHash, role.ContentHash, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            Apply(entity, role);
            entity.UpdatedAt = now;
            updated++;
        }

        var declared = roles.Select(role => role.Id).ToHashSet(StringComparer.Ordinal);
        var obsolete = existing.Values.Where(item => !declared.Contains(item.Id)).ToList();
        if (obsolete.Count > 0)
        {
            db.RoleViews.RemoveRange(obsolete);
        }

        if (added > 0 || updated > 0 || obsolete.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new RoleSnapshotSyncResult(added, updated, obsolete.Count, unchanged);
    }

    /// <summary>读取全部角色快照。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按 id 排序的角色快照。</returns>
    public async Task<IReadOnlyList<RoleViewEntity>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.RoleViews
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>构造新的快照行。</summary>
    /// <param name="role">角色声明。</param>
    /// <param name="now">写入时间。</param>
    private static RoleViewEntity CreateEntity(RoleDefinition role, string now)
    {
        var entity = new RoleViewEntity { Id = role.Id, LoadedAt = now };
        Apply(entity, role);
        entity.UpdatedAt = now;
        return entity;
    }

    /// <summary>把角色声明字段复制到快照行。</summary>
    /// <param name="entity">目标行。</param>
    /// <param name="role">角色声明。</param>
    private static void Apply(RoleViewEntity entity, RoleDefinition role)
    {
        entity.Name = role.Name;
        entity.Description = role.Description;
        entity.SourcePath = role.SourcePath;
        entity.ContentHash = role.ContentHash;
        entity.Skills = JsonSerializer.Serialize(role.Skills);
        entity.Tools = JsonSerializer.Serialize(role.Tools);
        entity.Delivery = role.Delivery;
        entity.RequiresApproval = role.RequiresApproval;
        entity.CanRequestInput = role.CanRequestInput;
        entity.MaxParallel = role.MaxParallel;
        entity.MaxAttempts = role.MaxAttempts;
        entity.MaxRounds = role.MaxRounds;
        entity.MaxToolCalls = role.MaxToolCalls;
        entity.MaxDurationSeconds = role.MaxDurationSeconds;
        entity.MaxInputTokens = role.MaxInputTokens;
        entity.Payload = role.Payload;
    }
}
