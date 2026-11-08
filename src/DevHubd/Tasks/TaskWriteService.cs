using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DevHubd.Core.Data;
using DevHubd.Core.Roles;
using DevHubd.Core.Tasks;
using DevHubd.Core.Workspaces;
using DevHubd.Mcp.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DevHubd.Tasks;

/// <summary>
/// 任务写入实现：把声明序列化为任务文件，原子写（临时文件 + rename）到
/// <c>&lt;workspace&gt;/.devhub/tasks/&lt;id&gt;.json</c>，由扫描器在下一轮拾取。
/// 不直接写数据库，避免绕过 <c>task_view</c> 的唯一来源（任务文件）。
/// </summary>
public sealed class TaskWriteService : ITaskWriteService
{
    private static readonly Regex _rolePattern = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.Compiled);
    private static readonly string[] _activeStatuses = { "pending", "claimed", "running" };
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly WorkspaceRegistry _registry;
    private readonly RoleSnapshotStore _roles;
    private readonly IDbContextFactory<DevHubDbContext> _contextFactory;

    /// <summary>注入工作区清单、角色快照与数据库上下文工厂。</summary>
    /// <param name="registry">工作区清单。</param>
    /// <param name="roles">角色快照存取。</param>
    /// <param name="contextFactory">上下文工厂（用于占用与去重判定）。</param>
    public TaskWriteService(
        WorkspaceRegistry registry,
        RoleSnapshotStore roles,
        IDbContextFactory<DevHubDbContext> contextFactory)
    {
        _registry = registry;
        _roles = roles;
        _contextFactory = contextFactory;
    }

    /// <summary>写入或覆盖任务文件。</summary>
    /// <param name="request">写入请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<TaskUpsertOutcome> UpsertAsync(
        TaskUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var loaded = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        var workspaces = loaded.Workspaces;
        var workspaceId = request.WorkspaceId?.Trim();

        WorkspaceDefinition workspace;
        if (!string.IsNullOrEmpty(workspaceId))
        {
            var match = workspaces.FirstOrDefault(item => string.Equals(item.Id, workspaceId, StringComparison.Ordinal));
            if (match is null)
            {
                return Failure(TaskUpsertStatus.UnknownWorkspace, workspaceId, request.Id, "unknown_workspace");
            }

            workspace = match;
        }
        else if (workspaces.Count == 1)
        {
            workspace = workspaces[0];
        }
        else if (workspaces.Count == 0)
        {
            return Failure(TaskUpsertStatus.UnknownWorkspace, string.Empty, request.Id, "no_workspace");
        }
        else
        {
            return new TaskUpsertOutcome(
                TaskUpsertStatus.AmbiguousWorkspace,
                string.Empty,
                request.Id,
                null,
                null,
                false,
                null,
                workspaces.Select(item => item.Id).ToArray());
        }

        var role = string.IsNullOrWhiteSpace(request.Role) ? "implementer" : request.Role.Trim();
        if (!_rolePattern.IsMatch(role))
        {
            return Failure(
                TaskUpsertStatus.UnknownRole,
                workspace.Id,
                request.Id,
                $"role 不匹配 ^[a-z][a-z0-9-]{{0,31}}$：{role}");
        }

        var declaredRoles = await _roles.ListAsync(cancellationToken).ConfigureAwait(false);
        var roleKnown = declaredRoles.Count == 0
            ? string.Equals(role, "implementer", StringComparison.Ordinal)
            : declaredRoles.Any(item => string.Equals(item.Id, role, StringComparison.Ordinal));

        if (!roleKnown)
        {
            return Failure(
                TaskUpsertStatus.UnknownRole,
                workspace.Id,
                request.Id,
                declaredRoles.Count == 0
                    ? "角色快照为空（未部署插件包），仅允许 implementer"
                    : $"角色未在 role_view 中声明：{role}");
        }

        var json = BuildJson(request, role);
        if (!TaskFileReader.TryParse(json, out var document, out var error) || document is null)
        {
            return Failure(
                TaskUpsertStatus.InvalidTask,
                workspace.Id,
                request.Id,
                error ?? "任务字段不合法");
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var active = await db.QueueItems
            .AsNoTracking()
            .AnyAsync(
                item => item.WorkspaceId == workspace.Id &&
                        item.TaskId == document.Id &&
                        _activeStatuses.Contains(item.Status),
                cancellationToken)
            .ConfigureAwait(false);

        if (active)
        {
            return Failure(TaskUpsertStatus.Conflict, workspace.Id, document.Id, "task_active");
        }

        Directory.CreateDirectory(workspace.TaskDirectory);
        var path = Path.Combine(workspace.TaskDirectory, $"{document.Id}.json");
        var temp = Path.Combine(workspace.TaskDirectory, $".{document.Id}.{Guid.NewGuid():N}.tmp");

        await File.WriteAllTextAsync(temp, json, _utf8, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        var dedupeKey = $"{workspace.Id}/{document.Id}/{document.ContentHash}";
        var queued = await db.QueueItems
            .AsNoTracking()
            .AnyAsync(item => item.DedupeKey == dedupeKey, cancellationToken)
            .ConfigureAwait(false);

        return new TaskUpsertOutcome(
            TaskUpsertStatus.Written,
            workspace.Id,
            document.Id,
            path,
            document.ContentHash,
            queued,
            null,
            null);
    }

    /// <summary>按任务文件字段白名单构造 JSON（camelCase，UTF-8）。</summary>
    /// <param name="request">写入请求。</param>
    /// <param name="role">已解析的角色 id。</param>
    private static string BuildJson(TaskUpsertRequest request, string role)
    {
        var payload = new JsonObject
        {
            ["id"] = request.Id,
            ["title"] = request.Title,
            ["status"] = "ready",
            ["priority"] = request.Priority,
            ["delivery"] = string.IsNullOrWhiteSpace(request.Delivery) ? "review" : request.Delivery.Trim(),
            ["prompt"] = request.Prompt,
            ["role"] = role,
            ["requiresApproval"] = request.RequiresApproval,
        };

        if (request.WriteScope is { Count: > 0 })
        {
            payload["writeScope"] = ToArray(request.WriteScope);
        }

        if (request.DependsOn is { Count: > 0 })
        {
            payload["dependsOn"] = ToArray(request.DependsOn);
        }

        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            payload["source"] = request.Source;
        }

        return payload.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>把字符串序列转成 JSON 数组节点。</summary>
    /// <param name="values">字符串序列。</param>
    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    /// <summary>构造失败结果。</summary>
    /// <param name="status">状态。</param>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="reason">原因。</param>
    private static TaskUpsertOutcome Failure(
        TaskUpsertStatus status,
        string workspaceId,
        string taskId,
        string reason) =>
        new(status, workspaceId, taskId, null, null, false, reason, null);

    /// <summary>尽力删除临时文件；失败只影响清理，不掩盖原始异常。</summary>
    /// <param name="path">临时文件路径。</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
