using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Data.Entities;
using DevHubd.Core.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Core.Tasks;

/// <summary>一次工作区扫描的结果。</summary>
/// <param name="Scanned">尝试读取的 .json 文件数。</param>
/// <param name="Accepted">通过校验并写入 task_view 的任务数。</param>
/// <param name="Invalid">校验失败被跳过的文件数。</param>
/// <param name="MarkedMissing">被标记为 missing 的任务数。</param>
/// <param name="Problems">每个非法文件的原因（供宿主记 Warning）。</param>
public sealed record TaskScanResult(int Scanned, int Accepted, int Invalid, int MarkedMissing, IReadOnlyList<string> Problems);

/// <summary>
/// 扫描器：把 <c>&lt;workspace&gt;/.devhub/tasks/*.json</c> 物化到 <c>task_view</c>。
/// 只读任务文件，不改工作区；同一文件重复扫描是幂等的（按 <c>(workspace_id, id)</c> 覆盖）。
/// 文件消失时把既有行标记为 <c>missing</c>，不删除历史（保留审计）。
/// 读写统一通过 EF Core（<see cref="DevHubDbContext"/>）完成，与其它 store 共用同一套映射。
/// </summary>
public sealed class TaskScanner
{
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入 EF Core 上下文工厂。</summary>
    /// <param name="contextFactory">上下文工厂。</param>
    public TaskScanner(IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>用裸连接工厂构造（测试与不依赖 DI 的场景）；内部转换为 EF 上下文工厂。</summary>
    /// <param name="factory">SQLite 连接工厂。</param>
    public TaskScanner(SqliteConnectionFactory factory)
        : this(new DevHubDbContextFactory(factory.ConnectionString))
    {
    }

    /// <summary>扫描一个工作区并落库。</summary>
    /// <param name="workspace">工作区定义。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<TaskScanResult> ScanAsync(WorkspaceDefinition workspace, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var accepted = new List<(string SourcePath, TaskDocument Document)>();
        var problems = new List<string>();
        var scanned = 0;

        if (Directory.Exists(workspace.TaskDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(workspace.TaskDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                scanned++;
                if (TaskFileReader.TryRead(path, out var document, out var error) && document is not null)
                {
                    accepted.Add((path, document));
                }
                else
                {
                    problems.Add($"{Path.GetFileName(path)}：{error}");
                }
            }
        }

        // 一个工作区的扫描是一个事务：要么整批可见，要么全部回滚（与原 IMMEDIATE 事务等价）。
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await db.TaskViews
            .Where(task => task.WorkspaceId == workspace.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rowsById = new Dictionary<string, TaskViewEntity>(StringComparer.Ordinal);
        foreach (var row in existing)
        {
            rowsById[row.Id] = row;
        }

        // 已交付（审批通过）的任务不允许被文件里的旧状态退回可执行态。
        // 这层防御针对历史遗留的"库已交付、文件未落"半成品；正常路径下审批已先写文件再提交。
        var deliveredIds = existing
            .Where(row => string.Equals(row.ApprovalState, "approved", StringComparison.Ordinal))
            .Select(row => row.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (sourcePath, document) in accepted)
        {
            if (!rowsById.TryGetValue(document.Id, out var row))
            {
                row = new TaskViewEntity
                {
                    WorkspaceId = workspace.Id,
                    Id = document.Id,
                    ApprovalState = "none",
                };
                db.TaskViews.Add(row);
                rowsById[document.Id] = row;
            }

            // 覆盖式写入：与原 ON CONFLICT (workspace_id, id) DO UPDATE 的列集合一一对应。
            row.SourcePath = sourcePath;
            row.ContentHash = document.ContentHash;
            row.Title = document.Title;
            row.Status = deliveredIds.Contains(document.Id) ? "done" : document.Status;
            row.Priority = document.Priority;
            row.WriteScope = JsonSerializer.Serialize(document.WriteScope);
            row.Payload = document.RawJson;
            row.RoleId = document.Role;
            row.DependsOn = document.DependsOn.Count == 0 ? null : JsonSerializer.Serialize(document.DependsOn);
            row.SeenAt = now;
            row.UpdatedAt = now;
        }

        var acceptedIds = accepted.Select(entry => entry.Document.Id).ToHashSet(StringComparer.Ordinal);
        var markedMissing = 0;

        foreach (var row in existing)
        {
            if (string.Equals(row.Status, "missing", StringComparison.Ordinal) ||
                acceptedIds.Contains(row.Id) ||
                deliveredIds.Contains(row.Id))
            {
                continue;
            }

            row.Status = "missing";
            row.UpdatedAt = now;
            markedMissing++;
        }

        // 依赖闸门：文件声明 ready 但前置任务尚未 done 时，任务视图按“有效状态”置为 blocked。
        // 每次扫描都从文件状态重新推导，所以前置任务完成后下一轮会自动回到 ready，无需回写用户文件。
        // 已交付任务不参与推导：它们必须保持 done。
        foreach (var (_, document) in accepted)
        {
            if (document.DependsOn.Count == 0 ||
                deliveredIds.Contains(document.Id) ||
                !string.Equals(document.Status, "ready", StringComparison.Ordinal))
            {
                continue;
            }

            var ready = document.DependsOn.All(dependency =>
                rowsById.TryGetValue(dependency, out var dependencyRow) &&
                string.Equals(dependencyRow.Status, "done", StringComparison.Ordinal));

            if (!ready)
            {
                var row = rowsById[document.Id];
                row.Status = "blocked";
                row.UpdatedAt = now;
            }
        }

        // 角色闸门：任务声明的角色必须是 role_view 中已声明的角色；否则置 blocked，绝不静默降级。
        // role_view 为空（部署未携带插件包）时按 v4 legacy 模式放行，避免整体停摆。
        var declaredRoles = await db.RoleViews
            .AsNoTracking()
            .Select(role => role.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (declaredRoles.Count > 0)
        {
            var knownRoles = declaredRoles.ToHashSet(StringComparer.Ordinal);
            foreach (var (_, document) in accepted)
            {
                if (!string.Equals(document.Status, "ready", StringComparison.Ordinal) ||
                    deliveredIds.Contains(document.Id) ||
                    knownRoles.Contains(document.Role))
                {
                    continue;
                }

                var row = rowsById[document.Id];
                row.Status = "blocked";
                row.UpdatedAt = now;
                problems.Add($"{document.Id}: 角色未在 role_view 中声明：{document.Role}");
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new TaskScanResult(scanned, accepted.Count, scanned - accepted.Count, markedMissing, problems);
    }
}
