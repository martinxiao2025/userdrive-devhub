using System.Reflection;
using System.Text.Json;
using DevHubd.Mcp.Contracts;
using DevHubd.Mcp.Resources;
using DevHubd.Mcp.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 工具面与资源面防漂移测试（借鉴参考实现的 <c>tests/model-surface.spec.ts</c>）。
/// 任何工具 / 资源改名、改描述、改参数都必须显式更新本文件。
/// </summary>
public sealed class ToolSurfaceSnapshotTests
{
    /// <summary>P3 工具面快照：工具名 → 参数名列表（顺序与声明顺序一致）。</summary>
    private static readonly Dictionary<string, string[]> _expectedTools = new(StringComparer.Ordinal)
    {
        ["scheduler_status"] = Array.Empty<string>(),
        ["queue_list"] = new[] { "status", "limit", "cursor" },
        ["queue_get"] = new[] { "queueItemId" },
        ["dispatch_now"] = new[] { "taskId", "workspaceId", "provider", "dryRun", "force" },
        ["scheduler_control"] = new[] { "action", "reason", "mode", "provider" },
        ["result_list"] = new[] { "unreadOnly", "limit", "cursor" },
        ["result_ack"] = new[] { "ids", "all" },
        ["input_request_list"] = new[] { "workspaceId", "taskId", "status", "limit", "cursor" },
        ["input_request_get"] = new[] { "id" },
        ["input_request_answer"] = new[] { "id", "answer", "optionId", "answeredBy", "idempotencyKey" },
        ["input_request_cancel"] = new[] { "id", "reason" },
        ["task_upsert"] = new[]
        {
            "id", "title", "prompt", "workspaceId", "priority", "role", "delivery",
            "writeScope", "dependsOn", "requiresApproval", "source",
        },
    };

    /// <summary>只读资源的 URI 模板快照（§11.2）。</summary>
    private static readonly string[] _expectedResources =
    {
        "devhub://limits",
        "devhub://results/unread",
        "devhub://scheduler/queue",
        "devhub://scheduler/status",
        "devhub://tasks",
    };

    /// <summary>程序集内所有 <c>[McpServerTool]</c> 方法的快照必须与约定一致。</summary>
    [Fact]
    public void ToolSurface_MatchesSnapshot()
    {
        var tools = typeof(SchedulerStatusTool).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<McpServerToolAttribute>(),
            })
            .Where(item => item.Attribute?.Name is not null)
            .ToDictionary(item => item.Attribute!.Name!, item => item.Method, StringComparer.Ordinal);

        Assert.Equal(_expectedTools.Keys.OrderBy(k => k), tools.Keys.OrderBy(k => k));

        foreach (var (name, method) in tools)
        {
            var parameters = method.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken))
                .Select(p => p.Name!)
                .ToArray();

            Assert.Equal(_expectedTools[name], parameters);
        }
    }

    /// <summary>每个工具都必须有非空描述，否则 Chat 侧无法判断何时调用。</summary>
    [Fact]
    public void ToolSurface_AllToolsHaveDescription()
    {
        var methods = typeof(SchedulerStatusTool).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name is not null);

        foreach (var method in methods)
        {
            var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrWhiteSpace(description), $"{method.Name} 缺少 Description");
        }
    }

    /// <summary>
    /// 每个工具都必须注入摘要源——摘要追加在工具内部完成，
    /// 结构上强制依赖可防止新增工具忘记追加尾部摘要（§11.4）。
    /// </summary>
    [Fact]
    public void ToolSurface_EveryToolCanAppendDigest()
    {
        var toolTypes = typeof(SchedulerStatusTool).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .ToArray();

        Assert.Equal(_expectedTools.Count, toolTypes.Length);

        foreach (var type in toolTypes)
        {
            var constructor = type.GetConstructors().Single();
            var hasDigest = constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(DevHubd.Mcp.Abstractions.IToolDigestSource));

            Assert.True(hasDigest, $"{type.Name} 的构造函数缺少 IToolDigestSource，工具结果将没有尾部摘要。");
        }
    }

    /// <summary>只读资源的 URI 模板必须与 §11.2 一致，且都有描述。</summary>
    [Fact]
    public void ResourceSurface_MatchesSnapshot()
    {
        var resources = typeof(DevHubResources).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<McpServerResourceAttribute>(),
            })
            .Where(item => item.Attribute?.UriTemplate is not null)
            .ToArray();

        Assert.Equal(
            _expectedResources,
            resources.Select(item => item.Attribute!.UriTemplate!).OrderBy(uri => uri, StringComparer.Ordinal));

        foreach (var item in resources)
        {
            var description = item.Method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrWhiteSpace(description), $"{item.Method.Name} 缺少 Description");
        }
    }

    /// <summary><c>scheduler_status</c> 的字段契约：§11.1 的关键键必须出现在序列化结果中。</summary>
    [Fact]
    public void StatusSnapshot_SerializesRequiredKeys()
    {
        var json = JsonSerializer.Serialize(SchedulerStatusSnapshotFactory(), ToolJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        foreach (var key in new[]
                 {
                     "enabled", "mode", "phase", "intervalSeconds", "counters", "queue",
                     "providers", "mcp", "host", "unread",
                 })
        {
            Assert.True(root.TryGetProperty(key, out _), $"缺少字段：{key}");
        }

        // null 字段按约定不序列化。
        Assert.False(root.TryGetProperty("pauseReason", out _));
        Assert.False(root.TryGetProperty("lastScanAt", out _));
    }

    /// <summary>枚举词表快照：工具描述与错误提示都从这里取值，改动必须显式登记。</summary>
    [Fact]
    public void Vocabulary_MatchesSnapshot()
    {
        Assert.Equal("pending/claimed/running/succeeded/failed/dead/cancelled/all", ToolVocabulary.QueueStatuses);
        Assert.Equal("auto/dispatch_only", ToolVocabulary.DeliveryModes);
        Assert.Equal(
            "start/pause/resume/scan_now/set_mode/reset_breaker/probe_models/status",
            ToolVocabulary.SchedulerActions);
        Assert.Equal("auto/openai/vscode", ToolVocabulary.Providers);
        Assert.Equal("closed/open/half_open", ToolVocabulary.BreakerStates);
        Assert.Equal("delivered/awaiting_review/failed", ToolVocabulary.ResultOutcomes);
        Assert.Equal("ready/blocked/done/cancelled/missing", ToolVocabulary.TaskStatuses);
        Assert.Equal("open/answered/expired/cancelled/all", ToolVocabulary.InputStatuses);
    }

    /// <summary>
    /// 用量与轮次耗尽必须显式出现在契约里：消费方不能靠“token 是否为 0”猜用量是否可得，
    /// 也不能把轮次耗尽的兜底答复当成自然完成。
    /// </summary>
    [Fact]
    public void UsageAndRoundsFlags_AreSerializedInContracts()
    {
        var attempt = new AttemptSummary(
            1,
            "openai",
            "gpt-4.1-mini",
            "2026-11-08T04:09:03.1234567Z",
            null,
            "succeeded",
            null,
            2,
            null,
            null,
            null,
            false,
            false,
            true);

        using var attemptDocument = JsonDocument.Parse(JsonSerializer.Serialize(attempt, ToolJson.Options));
        Assert.False(attemptDocument.RootElement.GetProperty("usageAvailable").GetBoolean());
        Assert.True(attemptDocument.RootElement.GetProperty("roundsExhausted").GetBoolean());

        var result = new ResultSummary(
            "r1",
            "t1",
            "q1",
            "openai",
            "awaiting_review",
            "摘要",
            Array.Empty<string>(),
            "2026-11-08T04:09:03.1234567Z",
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            true);

        using var resultDocument = JsonDocument.Parse(JsonSerializer.Serialize(result, ToolJson.Options));
        Assert.False(resultDocument.RootElement.GetProperty("usageAvailable").GetBoolean());
        Assert.True(resultDocument.RootElement.GetProperty("roundsExhausted").GetBoolean());
    }

    /// <summary>时间字段统一 UTC + Z：历史 <c>+00:00</c> 行在出契约时归一。</summary>
    [Fact]
    public void UtcTime_FormatsAndNormalizesToZ()
    {
        Assert.Equal(
            "2026-11-08T04:09:03.1234567Z",
            DevHubd.Core.Models.UtcTime.Normalize("2026-11-08T04:09:03.1234567+00:00"));
        Assert.EndsWith("Z", DevHubd.Core.Models.UtcTime.Now(), StringComparison.Ordinal);
        Assert.Null(DevHubd.Core.Models.UtcTime.Normalize(null));
        Assert.Equal("not-a-time", DevHubd.Core.Models.UtcTime.Normalize("not-a-time"));
    }

    /// <summary>构造一个最小快照用于序列化契约断言。</summary>
    private static SchedulerStatusSnapshot SchedulerStatusSnapshotFactory() => new()
    {
        IntervalSeconds = 30,
    };
}
