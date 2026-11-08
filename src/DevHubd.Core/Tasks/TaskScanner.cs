using System.Security.Cryptography;
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

        // 待审结果按（任务 id，分发时内容哈希）识别。它的存在说明"状态由审批决策决定"：
        // 文件因审批回写成功但库提交失败而短暂显示 done 时，扫描不得用文件覆盖库内状态，
        // 否则会出现"待审结果还在、任务视图却已 done"的另一种半边态。
        var pendingReviewPairs = (await db.Results
                .AsNoTracking()
                .Where(result => result.Outcome == "awaiting_review")
                .Join(
                    db.QueueItems.AsNoTracking(),
                    result => result.QueueItemId,
                    item => item.Id,
                    (result, item) => new { result.TaskId, item.TaskHash })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(entry => (entry.TaskId, entry.TaskHash))
            .ToHashSet();

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

            // 覆盖式写入前先记住旧值：审批态与待审判断都依赖"库里的上一次内容"。
            var previousHash = row.ContentHash;
            var previousPayload = row.Payload;
            var substantiveChange = !isNew && HasSubstantiveChange(previousPayload, document.RawJson);

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
                // 已审批任务的重做通道：只有实质字段变化才视为新需求，重置审批态并允许重新派发；
                // 仅 status（或格式）变化保持已交付，避免"把文件改回 ready"绕过审批语义。
                if (substantiveChange)
                {
                    row.ApprovalState = "none";
                    row.Status = document.Status;
                }
                else
                {
                    row.Status = "done";
                }

                continue;
            }

            if (!isNew && pendingReviewPairs.Contains((document.Id, previousHash)))
            {
                // 待审期间状态以库为准。内容只是格式/status 变化时保持原内容哈希，
                // 让入队闸门仍能按 hash 认出"这份内容已有待审结果"，不被重复派发。
                if (!substantiveChange)
                {
                    row.ContentHash = previousHash;
                }

                pendingReviewIds.Add(document.Id);
                continue;
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
    /// 判断任务文件的实质字段是否变化：忽略 <c>status</c> 与 JSON 格式（缩进、键序、换行），
    /// 只有 prompt / writeScope / role / 验收条件等需求字段变化才算"新需求"。
    /// 任一侧无法解析时按"未变化"保守处理，避免把格式损坏误判成新任务而触发重派。
    /// </summary>
    /// <param name="storedPayload">库中保存的上一次任务原文。</param>
    /// <param name="currentRawJson">本次扫描读到的新任务原文。</param>
    private static bool HasSubstantiveChange(string? storedPayload, string currentRawJson)
    {
        var previous = CanonicalizeWithoutStatus(storedPayload);
        var current = CanonicalizeWithoutStatus(currentRawJson);
        return previous is not null &&
               current is not null &&
               !string.Equals(previous, current, StringComparison.Ordinal);
    }

    /// <summary>
    /// 把任务 JSON 规范化成可比较的 SHA-256：去掉顶层 <c>status</c>，对象键按序数排序，
    /// 数组保持原顺序。返回 null 表示不是合法的 JSON 对象。
    /// </summary>
    /// <param name="json">任务原文。</param>
    private static string? CanonicalizeWithoutStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement
                             .EnumerateObject()
                             .Where(property => !string.Equals(property.Name, "status", StringComparison.Ordinal))
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
            }

            return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>递归写入规范化 JSON：对象键排序，数组与标量原样输出。</summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="element">JSON 节点。</param>
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
