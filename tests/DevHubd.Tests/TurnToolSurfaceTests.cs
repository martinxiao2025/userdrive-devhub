using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 角色工具面：<c>ToolDispatcher</c> 只暴露角色声明的工具，
/// 未声明即返回 <c>TOOL_NOT_ALLOWED</c>；服务层工具与文件工具共用同一白名单。
/// </summary>
public sealed class TurnToolSurfaceTests : IDisposable
{
    private readonly string _directory;
    private readonly TurnWorkspace _workspace;
    private readonly BudgetLimits _budget;

    /// <summary>准备隔离的读写目录与预算。</summary>
    public TurnToolSurfaceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _workspace = new TurnWorkspace(_directory, _directory, WriteScope: null, EnforceScope: false);
        _budget = new BudgetLimits(4, TimeSpan.FromSeconds(30), 5, 5, 4096, 4096);
    }

    /// <summary>清理临时目录。</summary>
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

    [Fact]
    public async Task TaskUpsert_WhenDeclared_IsVisibleAndInvocableAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new RecordingToolService();
        var dispatcher = CreateDispatcher(service, ["read_file", "task_upsert"]);

        Assert.Contains(dispatcher.Describe(), descriptor => descriptor.Name == "task_upsert");
        Assert.DoesNotContain(dispatcher.Describe(), descriptor => descriptor.Name == "write_file");

        var result = await dispatcher.ExecuteAsync(
            "turn-1",
            "task_upsert",
            JsonDocument.Parse("""{"id":"T-1"}""").RootElement,
            ct);

        Assert.True(result.Success);
        Assert.Equal("task_upsert", service.InvokedTool);
        Assert.Equal(1, dispatcher.ExecutedCalls);
    }

    [Fact]
    public async Task TaskUpsert_WhenNotDeclared_IsRejectedWithStableCodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new RecordingToolService();
        var dispatcher = CreateDispatcher(service, ["read_file", "write_file"]);

        Assert.DoesNotContain(dispatcher.Describe(), descriptor => descriptor.Name == "task_upsert");

        var result = await dispatcher.ExecuteAsync(
            "turn-1",
            "task_upsert",
            JsonDocument.Parse("""{"id":"T-1"}""").RootElement,
            ct);

        Assert.False(result.Success);
        Assert.Equal(DevHubErrorCodes.ToolNotAllowed, result.ErrorCode);
        Assert.Null(service.InvokedTool);
        Assert.Equal(0, dispatcher.ExecutedCalls);
    }

    [Fact]
    public void DefaultToolNames_AreExplicitAndIncludeTaskUpsertEntryPoints()
    {
        Assert.Contains("read_file", TurnRunner.DefaultToolNames);
        Assert.Contains("write_file", TurnRunner.DefaultToolNames);
        Assert.Contains("scheduler_status", TurnRunner.DefaultToolNames);
        Assert.Contains("queue_get", TurnRunner.DefaultToolNames);
        Assert.Contains("result_ack", TurnRunner.DefaultToolNames);
        Assert.Equal(TurnRunner.DefaultRoleId, "implementer");
    }

    /// <summary>构造带白名单与服务层端口的派发器。</summary>
    /// <param name="service">服务层工具端口。</param>
    /// <param name="allowed">允许的工具名。</param>
    private ToolDispatcher CreateDispatcher(ITurnToolService service, IReadOnlyCollection<string> allowed) =>
        new(
            _workspace,
            _budget,
            allowed,
            NullLogger<ToolDispatcher>.Instance,
            canRequestInput: false,
            askInput: null,
            serviceTools: service);

    /// <summary>记录调用的服务层工具端口。</summary>
    private sealed class RecordingToolService : ITurnToolService
    {
        private static readonly ToolDescriptor[] _descriptors =
        {
            new("task_upsert", "写入任务文件", """{"type":"object","properties":{"id":{"type":"string"}}}"""),
        };

        /// <summary>最近一次被调用的工具名。</summary>
        public string? InvokedTool { get; private set; }

        /// <inheritdoc />
        public IReadOnlyList<ToolDescriptor> Describe() => _descriptors;

        /// <inheritdoc />
        public Task<ToolExecutionResult> InvokeAsync(
            string toolName,
            JsonElement input,
            CancellationToken cancellationToken = default)
        {
            InvokedTool = toolName;
            return Task.FromResult(ToolExecutionResult.Ok("""{"taskId":"T-1"}"""));
        }
    }
}
