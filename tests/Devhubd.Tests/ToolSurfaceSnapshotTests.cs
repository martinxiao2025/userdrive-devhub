using System.Reflection;
using System.Text.Json;
using Devhubd.Mcp.Contracts;
using Devhubd.Mcp.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace Devhubd.Tests;

/// <summary>
/// 工具面防漂移测试（借鉴参考实现的 <c>tests/model-surface.spec.ts</c>，范围缩小到工具名 / 描述 / 参数）。
/// 任何工具改名、改描述、改参数都必须显式更新本文件。
/// </summary>
public sealed class ToolSurfaceSnapshotTests
{
    /// <summary>P0 工具面快照：工具名 → 参数名列表。</summary>
    private static readonly Dictionary<string, string[]> ExpectedTools = new(StringComparer.Ordinal)
    {
        ["scheduler_status"] = new[] { "verbose" },
    };

    /// <summary>程序集内所有 <c>[McpServerTool]</c> 方法的快照必须与 P0 约定一致。</summary>
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

        Assert.Equal(ExpectedTools.Keys.OrderBy(k => k), tools.Keys.OrderBy(k => k));

        foreach (var (name, method) in tools)
        {
            var parameters = method.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken))
                .Select(p => p.Name!)
                .ToArray();

            Assert.Equal(ExpectedTools[name], parameters);
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

    /// <summary>构造一个最小快照用于序列化契约断言。</summary>
    private static SchedulerStatusSnapshot SchedulerStatusSnapshotFactory() => new()
    {
        IntervalSeconds = 30,
    };
}
