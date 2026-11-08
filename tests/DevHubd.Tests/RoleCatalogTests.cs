using DevHubd.Core.Data;
using DevHubd.Core.Roles;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 角色声明加载与快照测试：真实插件包、非法声明、技能引用与 <c>role_view</c> 同步语义。
/// </summary>
public sealed class RoleCatalogTests : IDisposable
{
    private readonly List<string> _directories = new();

    /// <summary>清理本用例创建的临时目录。</summary>
    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 忽略：临时目录由系统回收。
            }
        }
    }

    /// <summary>仓库内真实插件包必须加载出三个内置角色，且技能引用全部存在。</summary>
    [Fact]
    public void Load_RealPluginDirectory_ReturnsBuiltInRoles()
    {
        var pluginDirectory = RoleCatalog.ResolvePluginDirectory();
        Assert.NotNull(pluginDirectory);

        var result = RoleCatalog.Load(pluginDirectory!);

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "implementer", "reviewer", "triage" }, result.Roles.Select(role => role.Id));
        Assert.All(result.Roles, role =>
        {
            Assert.Equal(64, role.ContentHash.Length);
            Assert.Equal(role.ContentHash.ToLowerInvariant(), role.ContentHash);
            Assert.NotEmpty(role.Skills);
            Assert.NotEmpty(role.Tools);
            Assert.InRange(role.MaxParallel, 1, 16);
            Assert.InRange(role.MaxAttempts, 1, 10);
        });
    }

    /// <summary>缺字段必须失败，并给出字段名，不能静默跳过该角色。</summary>
    [Fact]
    public void Load_MissingField_ReportsFieldName()
    {
        var pluginDirectory = CreatePlugin();
        WriteSkill(pluginDirectory, "devhub-scheduler");
        WriteRole(
            pluginDirectory,
            "broken",
            """
            {
              "id": "broken",
              "name": "缺预算",
              "description": "测试",
              "skills": ["devhub-scheduler"],
              "tools": ["queue_get"],
              "delivery": "dispatch_only",
              "requiresApproval": false,
              "canRequestInput": false,
              "concurrency": { "maxParallel": 1, "maxAttempts": 1 }
            }
            """);

        var result = RoleCatalog.Load(pluginDirectory);

        Assert.Empty(result.Roles);
        Assert.Contains(result.Errors, error => error.Contains("缺少必填字段：budget", StringComparison.Ordinal));
    }

    /// <summary>引用不存在的技能必须失败。</summary>
    [Fact]
    public void Load_UnknownSkill_Fails()
    {
        var pluginDirectory = CreatePlugin();
        WriteRole(
            pluginDirectory,
            "alpha",
            """
            {
              "id": "alpha",
              "name": "甲",
              "description": "测试",
              "skills": ["missing-skill"],
              "tools": ["queue_get"],
              "delivery": "dispatch_only",
              "requiresApproval": false,
              "canRequestInput": false,
              "concurrency": { "maxParallel": 1, "maxAttempts": 1 },
              "budget": { "maxRounds": 5, "maxToolCalls": 10, "maxDurationSeconds": 60 }
            }
            """);

        var result = RoleCatalog.Load(pluginDirectory);

        Assert.Empty(result.Roles);
        Assert.Contains(result.Errors, error => error.Contains("引用不存在的技能：missing-skill", StringComparison.Ordinal));
    }

    /// <summary>快照同步必须幂等，内容变化时更新哈希，文件删除时移除快照行。</summary>
    [Fact]
    public async Task Snapshot_SyncTracksFileChangesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var pluginDirectory = CreatePlugin();
        WriteSkill(pluginDirectory, "devhub-scheduler");
        WriteRole(pluginDirectory, "alpha", BuildRole("alpha", "甲"));
        WriteRole(pluginDirectory, "beta", BuildRole("beta", "乙"));

        var loaded = RoleCatalog.Load(pluginDirectory);
        Assert.Empty(loaded.Errors);

        var databasePath = Path.Combine(CreateDirectory(), "devhub.db");
        var connectionFactory = new SqliteConnectionFactory(databasePath);
        await Migrator.MigrateAsync(connectionFactory, ct);

        var store = new RoleSnapshotStore(new DevHubDbContextFactory(connectionFactory.ConnectionString));

        var first = await store.SyncAsync(loaded.Roles, ct);
        Assert.Equal(2, first.Added);
        Assert.Equal(0, first.Updated);

        var second = await store.SyncAsync(loaded.Roles, ct);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(2, second.Unchanged);

        WriteRole(pluginDirectory, "alpha", BuildRole("alpha", "甲·改"));
        var changed = RoleCatalog.Load(pluginDirectory);
        Assert.Empty(changed.Errors);

        var third = await store.SyncAsync(changed.Roles, ct);
        Assert.Equal(1, third.Updated);
        Assert.Equal(1, third.Unchanged);

        var rows = await store.ListAsync(ct);
        Assert.Equal("甲·改", rows.Single(row => row.Id == "alpha").Name);

        File.Delete(Path.Combine(pluginDirectory, "roles", "beta.json"));
        var removed = RoleCatalog.Load(pluginDirectory);
        Assert.Empty(removed.Errors);

        var fourth = await store.SyncAsync(removed.Roles, ct);
        Assert.Equal(1, fourth.Removed);
        Assert.Single(await store.ListAsync(ct));
    }

    /// <summary>构造最小合法角色 JSON。</summary>
    /// <param name="id">角色 id，同时作为技能引用之外的标识。</param>
    /// <param name="name">展示名。</param>
    private static string BuildRole(string id, string name) =>
        $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "description": "测试角色",
          "skills": ["devhub-scheduler"],
          "tools": ["queue_get"],
          "delivery": "dispatch_only",
          "requiresApproval": false,
          "canRequestInput": true,
          "concurrency": { "maxParallel": 1, "maxAttempts": 1 },
          "budget": { "maxRounds": 5, "maxToolCalls": 10, "maxDurationSeconds": 60 }
        }
        """;

    /// <summary>创建带 <c>plugin.json</c> 的临时插件包目录。</summary>
    private string CreatePlugin()
    {
        var directory = CreateDirectory();
        File.WriteAllText(Path.Combine(directory, "plugin.json"), "{ \"name\": \"test-plugin\" }");
        Directory.CreateDirectory(Path.Combine(directory, "roles"));
        return directory;
    }

    /// <summary>创建技能目录与 <c>SKILL.md</c>。</summary>
    /// <param name="pluginDirectory">插件包目录。</param>
    /// <param name="name">技能名。</param>
    private static void WriteSkill(string pluginDirectory, string name)
    {
        var directory = Path.Combine(pluginDirectory, "skills", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"# {name}\n");
    }

    /// <summary>写入角色文件（UTF-8 无 BOM）。</summary>
    /// <param name="pluginDirectory">插件包目录。</param>
    /// <param name="id">角色 id / 文件名。</param>
    /// <param name="json">角色 JSON 文本。</param>
    private static void WriteRole(string pluginDirectory, string id, string json)
    {
        File.WriteAllText(Path.Combine(pluginDirectory, "roles", $"{id}.json"), json);
    }

    /// <summary>创建并登记临时目录。</summary>
    private string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "devhubd-roles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }
}
