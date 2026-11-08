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

        // 待审结果按（workspace_id，task_id）识别——这是本工作区内的事实，不参与哈希比较。
        // 它的存在说明"状态由审批决策决定"：文件因审批回写成功但库提交失败而短暂显示 done 时，
        // 扫描不得用文件覆盖库内状态，否则会出现"待审结果还在、任务视图却已 done"的另一种半边态。
        // 结果表没有 workspace_id，必须经 queue_item 反查并按工作区过滤，避免同名任务跨工作区串味。
        var pendingReviewTaskIds = (await db.Results
                .AsNoTracking()
                .Where(result => result.Outcome == "awaiting_review")
                .Join(
                    db.QueueItems.AsNoTracking(),
                    result => result.QueueItemId,
                    item => item.Id,
                    (result, item) => new { item.WorkspaceId, result.TaskId })
                .Where(entry => entry.WorkspaceId == workspace.Id)
                .Select(entry => entry.TaskId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var pendingReviewIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (sourcePath, document) in accepted)
        {
            var isNew = !rowsById.TryGetValue(document.Id, out var row);
            if (row is null)
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

            // 覆盖式写入前先记住旧值：审批态与"是否实质变化"都依赖库里的上一次内容。
            var previousPayload = row.Payload;
            var substantiveChange = !isNew && HasSubstantiveChange(previousPayload, document);

            // 覆盖式写入：与原 ON CONFLICT (workspace_id, id) DO UPDATE 的列集合一一对应。
            row.SourcePath = sourcePath;
            row.ContentHash = document.ContentHash;
            row.Title = document.Title;
            row.Priority = document.Priority;
            row.WriteScope = JsonSerializer.Serialize(document.WriteScope);
            row.Payload = document.RawJson;
            row.RoleId = document.Role;
            row.DependsOn = document.DependsOn.Count == 0 ? null : JsonSerializer.Serialize(document.DependsOn);
            row.SeenAt = now;
            row.UpdatedAt = now;

            if (!isNew && string.Equals(row.ApprovalState, "approved", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(row.RerunPendingHash))
                {
                    // 待审期间登记过再执行需求：审批已落地，作为新需求重新进入 ready（正常情况下
                    // 审批决策已清掉标记，这里兜底处理历史数据）。
                    row.RerunPendingHash = string.Empty;
                    row.ApprovalState = "none";
                    row.Status = "ready";
                }
                else if (substantiveChange)
                {
                    // 已审批任务的重做通道：只有实质字段变化才视为新需求，重置审批态并允许重新派发；
                    // 仅 status / 标题 / 优先级 / 来源或格式变化保持已交付，避免绕过审批语义。
                    row.ApprovalState = "none";
                    row.Status = document.Status;
                }
                else
                {
                    row.Status = "done";
                }

                continue;
            }

            if (!isNew && pendingReviewTaskIds.Contains(document.Id))
            {
                // 待审期间状态以库为准；实质变化只登记"决策落地后需再跑一轮"，
                // 绝不与待审结果并行派发（并行会产生第二个待审结果，审批语义错位）。
                // 登记的是内容哈希而非布尔量：改回已审内容后哈希一致，决策时不会多跑一轮。
                if (substantiveChange)
                {
                    row.RerunPendingHash = document.ContentHash;
                }

                pendingReviewIds.Add(document.Id);
                continue;
            }

            // 结果丢失的残留：approval_state=pending 但已无对应待审结果（例如结果行被清理）。
            // 不静默改状态：收敛为 none、清掉再执行标记，并落一条可见通知。
            if (!isNew && string.Equals(row.ApprovalState, "pending", StringComparison.Ordinal))
            {
                ReconcileLostPending(db, workspace.Id, row, now);
            }

            row.Status = document.Status;
        }

        // 已经在循环里把"实质变更的已审批任务"重置为 none，这里只收集仍然有效的已交付任务。
        var deliveredIds = rowsById.Values
            .Where(row => string.Equals(row.ApprovalState, "approved", StringComparison.Ordinal))
            .Select(row => row.Id)
            .ToHashSet(StringComparer.Ordinal);

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

            // 文件已消失但审批态仍停在 pending，且没有待审结果：同样按残留收敛并通知。
            if (!pendingReviewTaskIds.Contains(row.Id) &&
                string.Equals(row.ApprovalState, "pending", StringComparison.Ordinal))
            {
                ReconcileLostPending(db, workspace.Id, row, now);
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
                pendingReviewIds.Contains(document.Id) ||
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
                    pendingReviewIds.Contains(document.Id) ||
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

    /// <summary>
    /// 收敛"审批态为 pending 但已无对应待审结果"的残留：重置为 none、清除再执行哈希，
    /// 并落一条用户可见的 notice（kind=failure），避免视图长期卡在待审、后续审批误触发再执行。
    /// </summary>
    /// <param name="db">扫描事务内的上下文。</param>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="row">任务视图行。</param>
    /// <param name="now">当前时间（ISO-8601）。</param>
    private static void ReconcileLostPending(DevHubDbContext db, string workspaceId, TaskViewEntity row, string now)
    {
        row.ApprovalState = "none";
        row.RerunPendingHash = string.Empty;
        row.UpdatedAt = now;

        db.Notices.Add(new NoticeEntity
        {
            DedupeKey = $"state:reconcile:{workspaceId}:{row.Id}:{now}",
            Kind = "failure",
            TaskId = row.Id,
            Title = $"任务 {row.Id} 的待审状态已收敛",
            Body = "approval_state=pending 但不存在对应的待审结果，已重置为 none 并清除再执行标记。",
            CreatedAt = now,
        });
    }

    /// <summary>
    /// 判断任务文件是否发生了"实质变化"：只比较影响执行语义的字段集合，
    /// 即 <c>prompt</c>、<c>writeScope</c>、<c>role</c>、验收条件（当前 schema 无独立字段，由 prompt 承载）、
    /// <c>delivery</c>、<c>requiresApproval</c>、<c>dependsOn</c>。
    /// <c>source</c>、<c>title</c>、<c>priority</c>、<c>status</c> 以及 JSON 缩进/键序不参与比较
    /// （<c>task.schema.json</c> 已写明 source 不参与调度）。
    /// <c>writeScope</c> 与 <c>dependsOn</c> 按集合比较，换序不算变化。
    /// 任一侧无法解析时按"未变化"保守处理，避免把格式损坏误判成新需求而触发重派。
    /// </summary>
    /// <param name="storedPayload">库中保存的上一次任务原文。</param>
    /// <param name="current">本次扫描解析出的任务文档。</param>
    private static bool HasSubstantiveChange(string? storedPayload, TaskDocument current)
    {
        if (string.IsNullOrWhiteSpace(storedPayload) ||
            !TaskFileReader.TryParse(storedPayload, out var previous, out _) ||
            previous is null)
        {
            return false;
        }

        return !string.Equals(previous.Prompt, current.Prompt, StringComparison.Ordinal)
               || !string.Equals(previous.Role, current.Role, StringComparison.Ordinal)
               || !string.Equals(previous.Delivery, current.Delivery, StringComparison.Ordinal)
               || previous.RequiresApproval != current.RequiresApproval
               || !SetEquals(previous.WriteScope, current.WriteScope)
               || !SetEquals(previous.DependsOn, current.DependsOn);
    }

    /// <summary>按集合语义比较两个字符串列表：排序后逐项比较，换序不算变化。</summary>
    /// <param name="left">左侧集合。</param>
    /// <param name="right">右侧集合。</param>
    private static bool SetEquals(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);
}
