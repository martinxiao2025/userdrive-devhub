using System.Text;
using System.Text.Json;
using DevHubd.Core.Data;
using DevHubd.Core.Roles;
using DevHubd.Core.Workspaces;
using DevHubd.Mcp.Abstractions;
using DevHubd.Tasks;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// <c>task_upsert</c> 的任务写入服务测试：原子写入、字段落盘、角色校验与执行中冲突。
/// </summary>
public sealed class TaskWriteServiceTests : IDisposable
{
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly WorkspaceRegistry _registry;
    private readonly RoleSnapshotStore _roles;
    private readonly TaskWriteService _service;

    /// <summary>创建临时数据目录、迁移数据库、写入工作区清单并播种角色快照。</summary>
    public TaskWriteServiceTests()
    {
        var ct = TestContext.Current.CancellationToken;
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-task-write", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _connectionFactory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_connectionFactory, ct).GetAwaiter().GetResult();

        _registry = new WorkspaceRegistry(Path.Combine(_directory, "workspaces.json"));
        var contextFactory = new DevHubDbContextFactory(_connectionFactory.ConnectionString);
        _roles = new RoleSnapshotStore(contextFactory);
        _roles.SyncAsync(new[] { CreateRole("implementer"), CreateRole("reviewer") }, ct).GetAwaiter().GetResult();

        _service = new TaskWriteService(_registry, _roles, contextFactory);
    }

    /// <summary>删除临时目录。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }
    }

    /// <summary>合法请求必须原子写入任务文件，字段与内容哈希可回读，且文件不带 BOM。</summary>
    [Fact]
    public async Task Upsert_WritesTaskFileAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));

        var outcome = await _service.UpsertAsync(
            new TaskUpsertRequest(
                "alpha",
                "T-100",
                "接入评测",
                "实现评测入口",
                30,
                "reviewer",
                "auto",
                new[] { "src/" },
                new[] { "T-001" },
                true,
                "conversation"),
            ct);

        Assert.Equal(TaskUpsertStatus.Written, outcome.Status);
        Assert.NotNull(outcome.Path);
        Assert.False(outcome.Queued);
        Assert.True(File.Exists(outcome.Path));

        var bytes = await File.ReadAllBytesAsync(outcome.Path!, ct);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        using var document = JsonDocument.Parse(bytes);
        var rootElement = document.RootElement;
        Assert.Equal("T-100", rootElement.GetProperty("id").GetString());
        Assert.Equal("ready", rootElement.GetProperty("status").GetString());
        Assert.Equal("reviewer", rootElement.GetProperty("role").GetString());
        Assert.Equal("auto", rootElement.GetProperty("delivery").GetString());
        Assert.True(rootElement.GetProperty("requiresApproval").GetBoolean());
        Assert.Equal("T-001", rootElement.GetProperty("dependsOn")[0].GetString());
        Assert.Equal("conversation", rootElement.GetProperty("source").GetString());
        Assert.Equal(64, outcome.ContentHash!.Length);
    }

    /// <summary>未声明的角色必须失败，且不落盘任何任务文件。</summary>
    [Fact]
    public async Task Upsert_UnknownRole_FailsWithoutWritingAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));

        var outcome = await _service.UpsertAsync(
            new TaskUpsertRequest(null, "T-101", "未知角色", "执行", 0, "ghost", null, null, null, false, null),
            ct);

        Assert.Equal(TaskUpsertStatus.UnknownRole, outcome.Status);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, ".devhub", "tasks")));
    }

    /// <summary>存在未结束队列项时必须拒绝改写。</summary>
    [Fact]
    public async Task Upsert_WhenTaskIsActive_ReturnsConflictAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace("alpha");
        WriteWorkspaces(("alpha", root));

        var request = new TaskUpsertRequest(
            null, "T-102", "执行中", "执行", 0, null, null, null, null, false, null);
        Assert.Equal(TaskUpsertStatus.Written, (await _service.UpsertAsync(request, ct)).Status);

        await ExecuteAsync(
            """
            INSERT INTO queue_item
              (id, workspace_id, task_id, task_hash, dedupe_key, status, priority, attempt, max_attempts,
               preferred_provider, role_id, available_at, created_at, updated_at)
            VALUES
              ('q1', 'alpha', 'T-102', 'h', 'alpha/T-102/h', 'running', 0, 1, 3,
               'openai', 'implementer', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """,
            ct);

        var outcome = await _service.UpsertAsync(request with { Title = "改标题" }, ct);

        Assert.Equal(TaskUpsertStatus.Conflict, outcome.Status);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(root, ".devhub", "tasks", "T-102.json"),
            ct));
        Assert.Equal("执行中", document.RootElement.GetProperty("title").GetString());
    }

    /// <summary>多个已启用工作区且未指定 id 时必须返回歧义，并给出候选。</summary>
    [Fact]
    public async Task Upsert_MultipleWorkspaces_ReturnsAmbiguousAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteWorkspaces(("alpha", CreateWorkspace("alpha")), ("beta", CreateWorkspace("beta")));

        var outcome = await _service.UpsertAsync(
            new TaskUpsertRequest(null, "T-103", "歧义", "执行", 0, null, null, null, null, false, null),
            ct);

        Assert.Equal(TaskUpsertStatus.AmbiguousWorkspace, outcome.Status);
        Assert.Equal(new[] { "alpha", "beta" }, outcome.Candidates);
    }

    /// <summary>构造一个只用于快照的最小角色声明。</summary>
    /// <param name="id">角色 id。</param>
    private static RoleDefinition CreateRole(string id) => new(
        id,
        id,
        "测试角色",
        Array.Empty<string>(),
        new[] { "queue_get" },
        "dispatch_only",
        false,
        true,
        1,
        3,
        10,
        20,
        300,
        null,
        $"test/{id}.json",
        $"hash-{id}",
        "{}");

    /// <summary>创建带 <c>.devhub/tasks</c> 的工作区目录。</summary>
    /// <param name="id">工作区 id。</param>
    private string CreateWorkspace(string id)
    {
        var root = Path.Combine(_directory, id);
        Directory.CreateDirectory(Path.Combine(root, ".devhub", "tasks"));
        return root;
    }

    /// <summary>写入 workspaces.json。</summary>
    /// <param name="entries">(id, root) 列表。</param>
    private void WriteWorkspaces(params (string Id, string Root)[] entries)
    {
        var payload = new
        {
            version = 1,
            workspaces = entries.Select(entry => new { id = entry.Id, root = entry.Root, enabled = true }).ToArray(),
        };

        File.WriteAllText(
            Path.Combine(_directory, "workspaces.json"),
            JsonSerializer.Serialize(payload),
            _utf8);
    }

    /// <summary>执行一条 SQL。</summary>
    /// <param name="sql">SQL 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
