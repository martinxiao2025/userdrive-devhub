using System.Net;
using System.Text;
using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// OpenAI 兼容客户端的 usage 汇总语义：多轮工具调用必须跨轮累加；
/// 任意一轮缺失 usage 时整回合记为 null，绝不把部分值当成真实总量。
/// </summary>
public sealed class OpenAiUsageTests
{
    /// <summary>两轮都带 usage 时，返回两轮之和且标记为真实值。</summary>
    [Fact]
    public async Task RunTurnAsync_SumsUsageAcrossToolRoundsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHandler(
            """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"write_file","arguments":"{}"}}]}}],"usage":{"prompt_tokens":100,"completion_tokens":10}}
            """,
            """
            {"choices":[{"message":{"role":"assistant","content":"done"}}],"usage":{"prompt_tokens":200,"completion_tokens":20}}
            """);

        var result = await RunAsync(handler, ct);

        Assert.Equal(TurnState.Done, result.State);
        Assert.NotNull(result.Usage);
        Assert.Equal(300, result.Usage!.InputTokens);
        Assert.Equal(30, result.Usage.OutputTokens);
        Assert.False(result.Usage.Estimated);
    }

    /// <summary>最后一轮缺少 usage 时，整回合记为 null（无法得知精确总量）。</summary>
    [Fact]
    public async Task RunTurnAsync_MissingUsageInAnyRound_ReturnsNullAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHandler(
            """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"write_file","arguments":"{}"}}]}}],"usage":{"prompt_tokens":100,"completion_tokens":10}}
            """,
            """
            {"choices":[{"message":{"role":"assistant","content":"done"}}]}
            """);

        var result = await RunAsync(handler, ct);

        Assert.Equal(TurnState.Done, result.State);
        Assert.Null(result.Usage);
    }

    /// <summary>构造客户端并执行一次回合。</summary>
    /// <param name="handler">脚本化 HTTP 处理器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task<TurnResult> RunAsync(ScriptedHandler handler, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleModelClient(
            http,
            new OpenAiModelOptions { BaseUrl = "https://example.invalid/v1", Model = "test-model" },
            new StaticApiKeyProvider("test-key"),
            NullLogger<OpenAiCompatibleModelClient>.Instance);

        var request = new TurnRequest(
            "turn-1",
            "turn-1",
            OpenAiCompatibleModelClient.ProviderName,
            "queue-1",
            "ws-1",
            "task-1",
            "system",
            "user",
            new[] { new ToolDescriptor("write_file", "写文件", """{"type":"object"}""") },
            new BudgetLimits(4, TimeSpan.FromSeconds(10), 10, 10, 1024 * 1024, 1024 * 1024),
            Model: null,
            Timeout: TimeSpan.FromSeconds(5));

        return await client.RunTurnAsync(request, new NullDispatcher(), cancellationToken);
    }

    /// <summary>按顺序回放固定响应的 HTTP 处理器。</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        /// <summary>注入响应体序列。</summary>
        /// <param name="responses">依次返回的 JSON 响应体。</param>
        public ScriptedHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = _responses.Count > 0 ? _responses.Dequeue() : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>固定返回 key 的提供者（测试专用，不读取环境变量）。</summary>
    private sealed class StaticApiKeyProvider : IApiKeyProvider
    {
        private readonly string _key;

        /// <summary>注入固定 key。</summary>
        /// <param name="key">key 取值。</param>
        public StaticApiKeyProvider(string key)
        {
            _key = key;
        }

        /// <inheritdoc />
        public string EnvironmentVariableName => "DEVHUB_TEST_API_KEY";

        /// <inheritdoc />
        public string? GetApiKey() => _key;
    }

    /// <summary>不做任何事的工具执行器：只回显成功，便于驱动多轮循环。</summary>
    private sealed class NullDispatcher : IToolDispatcher
    {
        /// <inheritdoc />
        public IReadOnlyList<string> ChangedFiles => Array.Empty<string>();

        /// <inheritdoc />
        public int ExecutedCalls => 0;

        /// <inheritdoc />
        public IReadOnlyList<ToolDescriptor> Describe() => Array.Empty<ToolDescriptor>();

        /// <inheritdoc />
        public Task<ToolExecutionResult> ExecuteAsync(
            string turnId,
            string toolName,
            JsonElement input,
            CancellationToken cancellationToken) =>
            Task.FromResult(ToolExecutionResult.Ok("ok"));
    }
}
