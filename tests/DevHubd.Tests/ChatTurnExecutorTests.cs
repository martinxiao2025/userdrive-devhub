using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// U3 模型通道重构的契约测试：固定 <c>FunctionInvokingChatClient</c> 的轮次耗尽语义、
/// <c>FunctionInvoker</c> 的工具级错误映射、取消传播，以及脱敏日志不泄漏参数。
/// </summary>
public sealed class ChatTurnExecutorTests
{
    private static readonly ToolDescriptor _writeTool = new(
        "write_file",
        "写入文件",
        """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");

    /// <summary>
    /// 轮次上限耗尽时框架不抛异常：最后一轮移除工具再调用一次，返回最终文本。
    /// 这是替换手写循环后必须固定的行为（旧实现返回 TOOL_ROUNDS_EXCEEDED）。
    /// </summary>
    [Fact]
    public async Task RunAsync_IterationsExhausted_ReturnsFinalResponseWithoutToolsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new ScriptedDispatcher(_ => ToolExecutionResult.Ok("written"));
        var inner = new ScriptedChatClient(index => index < 2
            ? ToolCall("write_file")
            : Final("已达到工具轮数上限，以下为最终答复。"));

        var request = CreateRequest(maxRounds: 2);
        using var pipeline = ChatTurnExecutor.CreatePipeline(inner, request, dispatcher, NullLogger.Instance);

        var result = await ChatTurnExecutor.RunAsync(
            pipeline,
            request,
            dispatcher,
            MapTestException,
            NullLogger.Instance,
            ct);

        Assert.Equal(TurnState.Done, result.State);
        Assert.Equal("已达到工具轮数上限，以下为最终答复。", result.Text);
        Assert.Equal(2, result.ToolCalls);
        Assert.Equal(3, inner.Calls);
        Assert.True(inner.Requests[^1]?.Tools is null or { Count: 0 });

        // 状态仍是 Done（不改写框架返回路径），但必须留痕，供落库与 result_list 区分“自然完成”。
        Assert.True(result.RoundsExhausted);
    }

    /// <summary>工具业务失败以稳定错误码回灌给模型，模型可以据此纠正并完成回合。</summary>
    [Fact]
    public async Task RunAsync_ToolFails_FeedsErrorBackAndRecoversAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new ScriptedDispatcher(_ => ToolExecutionResult.Fail("FILE_NOT_FOUND", "文件不存在"));
        var inner = new ScriptedChatClient(index => index == 0
            ? ToolCall("write_file")
            : Final("已根据错误提示调整。"));

        var request = CreateRequest(maxRounds: 4);
        using var pipeline = ChatTurnExecutor.CreatePipeline(inner, request, dispatcher, NullLogger.Instance);

        var result = await ChatTurnExecutor.RunAsync(
            pipeline,
            request,
            dispatcher,
            MapTestException,
            NullLogger.Instance,
            ct);

        Assert.Equal(TurnState.Done, result.State);
        Assert.Equal("已根据错误提示调整。", result.Text);
        Assert.Equal(1, result.ToolCalls);
        Assert.False(result.RoundsExhausted);
    }

    /// <summary>连续工具错误达到框架上限后，框架原样抛出最后一次受控异常，执行器映射为 tool_error。</summary>
    [Fact]
    public async Task RunAsync_ConsecutiveToolErrors_ReturnsToolErrorAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new ScriptedDispatcher(_ => ToolExecutionResult.Fail("FILE_NOT_FOUND", "文件不存在"));
        var inner = new ScriptedChatClient(_ => ToolCall("write_file"));

        var request = CreateRequest(maxRounds: 10);
        using var pipeline = ChatTurnExecutor.CreatePipeline(inner, request, dispatcher, NullLogger.Instance);

        var result = await ChatTurnExecutor.RunAsync(
            pipeline,
            request,
            dispatcher,
            MapTestException,
            NullLogger.Instance,
            ct);

        Assert.Equal(TurnState.Failed, result.State);
        Assert.Equal(ModelErrorKinds.ToolError, result.Error!.Kind);
        Assert.Equal("FILE_NOT_FOUND", result.Error.Code);
        Assert.True(dispatcher.ExecutedCalls >= 3);
    }

    /// <summary>取消令牌已取消时，执行器必须原样抛出 OCE，而不是包装成 TurnResult。</summary>
    [Fact]
    public async Task RunAsync_TokenCancelled_PropagatesOperationCanceledExceptionAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new ScriptedDispatcher(_ => ToolExecutionResult.Ok("ok"));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inner = new ScriptedChatClient(_ =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        var request = CreateRequest(maxRounds: 4);
        using var pipeline = ChatTurnExecutor.CreatePipeline(inner, request, dispatcher, NullLogger.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChatTurnExecutor.RunAsync(
            pipeline,
            request,
            dispatcher,
            MapTestException,
            NullLogger.Instance,
            cancel.Token));
    }

    /// <summary>脱敏日志只记录摘要，工具参数与文件内容不得进入日志。</summary>
    [Fact]
    public async Task RunAsync_SanitizedLogging_DoesNotLeakToolArgumentsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new ScriptedDispatcher(_ => ToolExecutionResult.Ok("written"));
        var inner = new ScriptedChatClient(index => index == 0
            ? ToolCall("write_file", new Dictionary<string, object?>
            {
                ["path"] = "a.txt",
                ["content"] = "SECRET_TOKEN_VALUE",
            })
            : Final("完成"));

        var logger = new FakeLogger<SanitizedLoggingChatClient>();
        var request = CreateRequest(maxRounds: 4);
        using var pipeline = ChatTurnExecutor.CreatePipeline(
            inner,
            request,
            dispatcher,
            NullLogger.Instance,
            client => new SanitizedLoggingChatClient(client, "test", logger));

        var result = await ChatTurnExecutor.RunAsync(
            pipeline,
            request,
            dispatcher,
            MapTestException,
            NullLogger.Instance,
            ct);

        Assert.Equal(TurnState.Done, result.State);
        var records = logger.Collector.GetSnapshot();
        Assert.NotEmpty(records);
        Assert.DoesNotContain(records, record => record.Message.Contains("SECRET_TOKEN_VALUE", StringComparison.Ordinal));
    }

    /// <summary>构造一个最小回合请求。</summary>
    /// <param name="maxRounds">工具轮数上限。</param>
    private static TurnRequest CreateRequest(int maxRounds) => new(
        "turn-1",
        "session-1",
        "test",
        "queue-1",
        "workspace-1",
        "task-1",
        "系统提示",
        "用户指令",
        [_writeTool],
        new BudgetLimits(maxRounds, TimeSpan.FromSeconds(30), 5, 5, 4096, 4096),
        "test-model",
        TimeSpan.FromSeconds(30));

    /// <summary>测试用异常映射：把未知异常收敛为一个稳定错误。</summary>
    /// <param name="exception">异常。</param>
    private static ModelError MapTestException(Exception exception) =>
        new(ModelErrorKinds.ModelError, DevHubErrorCodes.TurnException, exception.GetType().Name);

    /// <summary>构造一次工具调用的响应。</summary>
    /// <param name="name">工具名。</param>
    /// <param name="arguments">参数。</param>
    private static ChatResponse ToolCall(string name, IDictionary<string, object?>? arguments = null) =>
        new(new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent($"call_{Guid.NewGuid():N}", name, arguments),
        }));

    /// <summary>构造最终文本响应。</summary>
    /// <param name="text">文本。</param>
    private static ChatResponse Final(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>按调用序号返回预设响应的假客户端。</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Func<int, ChatResponse> _responder;
        private int _calls;

        /// <summary>绑定响应脚本。</summary>
        /// <param name="responder">按调用序号返回响应。</param>
        public ScriptedChatClient(Func<int, ChatResponse> responder)
        {
            _responder = responder;
        }

        /// <summary>已调用次数。</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>每次调用收到的选项（用于断言最后一轮已移除工具）。</summary>
        public List<ChatOptions?> Requests { get; } = [];

        /// <inheritdoc />
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(options);
            var index = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(_responder(index));
        }

        /// <inheritdoc />
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }

    /// <summary>记录调用次数并按指定策略返回结果的假工具派发器。</summary>
    private sealed class ScriptedDispatcher : IToolDispatcher
    {
        private readonly Func<string, ToolExecutionResult> _result;

        /// <summary>绑定执行策略。</summary>
        /// <param name="result">按工具名返回结果。</param>
        public ScriptedDispatcher(Func<string, ToolExecutionResult> result)
        {
            _result = result;
        }

        /// <inheritdoc />
        public IReadOnlyList<string> ChangedFiles => Array.Empty<string>();

        /// <inheritdoc />
        public int ExecutedCalls { get; private set; }

        /// <inheritdoc />
        public IReadOnlyList<ToolDescriptor> Describe() => [_writeTool];

        /// <inheritdoc />
        public Task<ToolExecutionResult> ExecuteAsync(
            string turnId,
            string toolName,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            ExecutedCalls++;
            return Task.FromResult(_result(toolName));
        }
    }
}
