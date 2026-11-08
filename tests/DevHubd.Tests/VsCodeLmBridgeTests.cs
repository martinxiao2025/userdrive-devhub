using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DevHubd.Core;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// <c>vscode</c> 回合桥的协议级验收：发现文件 + <c>/health</c> 的 pid 校验、
/// 回合 <c>POST /turns</c> → 轮询终态、桥关闭时的错误分类。
/// 真实的 VS Code 扩展桥无法在无 GUI 环境中启动，本测试用最小 TCP 服务模拟协议；
/// 真实扩展联调在 VS Code Stable 1.145.2 上单独验证（见 README §6.1）。
/// </summary>
public sealed class VsCodeLmBridgeTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "devhubd-tests",
        Guid.NewGuid().ToString("N"));

    private DevHubPaths _paths = null!;

    /// <summary>准备临时数据目录。</summary>
    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _paths = new DevHubPaths(_directory);
        return ValueTask.CompletedTask;
    }

    /// <summary>清理临时目录。</summary>
    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>没有发现文件时探测返回 down，且原因可诊断。</summary>
    [Fact]
    public async Task Probe_WithoutDiscoveryFile_ReturnsDownAsync()
    {
        var client = CreateClient();
        var health = await client.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(health.Available);
        Assert.Equal(ProviderStates.Down, health.State);
        Assert.Contains("lm-bridge.json", health.Detail, StringComparison.Ordinal);
    }

    /// <summary>发现文件的 pid 与 /health 返回的 pid 不一致时判定为残留文件。</summary>
    [Fact]
    public async Task Probe_WithPidMismatch_ReturnsDownAsync()
    {
        await using var bridge = await FakeBridge.StartAsync(FakeBridge.CorrectPid);
        WriteDiscovery(pid: FakeBridge.CorrectPid + 1, bridge.Port);

        var health = await CreateClient().ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(health.Available);
        Assert.Contains("不一致", health.Detail, StringComparison.Ordinal);
    }

    /// <summary>pid 一致且返回模型名时判定 ready。</summary>
    [Fact]
    public async Task Probe_WithMatchingPid_ReturnsReadyAsync()
    {
        await using var bridge = await FakeBridge.StartAsync(FakeBridge.CorrectPid);
        WriteDiscovery(pid: FakeBridge.CorrectPid, bridge.Port);

        var health = await CreateClient().ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(health.Available);
        Assert.Equal(ProviderStates.Ready, health.State);
        Assert.Equal("fake-model", health.Model);
    }

    /// <summary>桥不在线时回合返回 bridge_down，不抛异常。</summary>
    [Fact]
    public async Task RunTurn_WithoutBridge_ReturnsBridgeDownAsync()
    {
        var result = await CreateClient().RunTurnAsync(
            CreateRequest(),
            new NoopDispatcher(),
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnState.Failed, result.State);
        Assert.Equal(ModelErrorKinds.BridgeDown, result.Error!.Kind);
    }

    /// <summary>桥返回 done 时回合成功，并带回文本、工具次数与改动文件。</summary>
    [Fact]
    public async Task RunTurn_WithBridge_ReturnsDoneAsync()
    {
        await using var bridge = await FakeBridge.StartAsync(FakeBridge.CorrectPid);
        WriteDiscovery(pid: FakeBridge.CorrectPid, bridge.Port);

        var result = await CreateClient().RunTurnAsync(
            CreateRequest(),
            new NoopDispatcher(),
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnState.Done, result.State);
        Assert.Equal("桥完成", result.Text);
        Assert.Equal(2, result.ToolCalls);
        Assert.Equal(new[] { "a.txt" }, result.FilesChanged);
        Assert.Equal(11, result.Usage!.InputTokens);
        Assert.False(result.Usage.Estimated);
    }

    /// <summary>
    /// 桥未标注 <c>usageSource=provider</c>（扩展拿不到真实 usage）时，
    /// 即使响应里带了 token 数字也不采信，Usage 为 null。
    /// </summary>
    [Fact]
    public async Task RunTurn_WithoutProviderUsage_LeavesUsageNullAsync()
    {
        await using var bridge = await FakeBridge.StartAsync(FakeBridge.CorrectPid, usageSource: "none");
        WriteDiscovery(pid: FakeBridge.CorrectPid, bridge.Port);

        var result = await CreateClient().RunTurnAsync(
            CreateRequest(),
            new NoopDispatcher(),
            TestContext.Current.CancellationToken);

        Assert.Equal(TurnState.Done, result.State);
        Assert.Null(result.Usage);
    }

    /// <summary>
    /// 取消回合时桥对 DELETE 无响应：取消请求必须有独立超时，
    /// 否则共享 HttpClient（无超时）会让 RunTurnAsync 永久挂起。
    /// </summary>
    [Fact]
    public async Task RunTurn_CancelWithStalledBridge_ReturnsCancelledWithinTimeoutAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteDiscovery(pid: 1, port: 1);

        using var http = new HttpClient(new StallingBridgeHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new VsCodeLmBridgeClient(
            http,
            _paths,
            new InternalApiOptions("http://127.0.0.1:3091", "internal-token"),
            new TurnDispatcherRegistry(),
            NullLogger<VsCodeLmBridgeClient>.Instance);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var turn = client.RunTurnAsync(CreateRequest(), new NoopDispatcher(), cancel.Token);
        await Task.Delay(100, ct);
        await cancel.CancelAsync();

        var result = await turn.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(TurnState.Cancelled, result.State);
        Assert.Equal(ModelErrorKinds.UserCancelled, result.Error!.Kind);
    }

    /// <summary>写出发现文件（camelCase，与扩展约定一致）。</summary>
    /// <param name="pid">pid。</param>
    /// <param name="port">端口。</param>
    private void WriteDiscovery(int pid, int port)
    {
        File.WriteAllText(
            _paths.LmBridgeFilePath,
            JsonSerializer.Serialize(new
            {
                pid,
                port,
                token = "test-token",
                model = (string?)null,
                startedAt = DateTimeOffset.UtcNow.ToString("O"),
            }),
            new UTF8Encoding(false));
    }

    /// <summary>构造桥客户端。</summary>
    private VsCodeLmBridgeClient CreateClient() => new(
        new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
        _paths,
        new InternalApiOptions("http://127.0.0.1:3091", "internal-token"),
        new TurnDispatcherRegistry(),
        NullLogger<VsCodeLmBridgeClient>.Instance);

    /// <summary>构造一个最小回合请求。</summary>
    private static TurnRequest CreateRequest() => new(
        "turn-1",
        "turn-1",
        VsCodeLmBridgeClient.ProviderId,
        "q1",
        "alpha",
        "T-001",
        "系统提示",
        "执行任务",
        Array.Empty<ToolDescriptor>(),
        new BudgetLimits(4, TimeSpan.FromSeconds(30), 5, 20, 1_000_000, 1_000_000),
        null,
        TimeSpan.FromSeconds(10));

    /// <summary>不执行任何工具的派发器。</summary>
    private sealed class NoopDispatcher : IToolDispatcher
    {
        /// <summary>改动文件（空）。</summary>
        public IReadOnlyList<string> ChangedFiles => Array.Empty<string>();

        /// <summary>已执行的工具调用次数（0）。</summary>
        public int ExecutedCalls => 0;

        /// <summary>工具白名单（空）。</summary>
        public IReadOnlyList<ToolDescriptor> Describe() => Array.Empty<ToolDescriptor>();

        /// <summary>拒绝执行。</summary>
        public Task<ToolExecutionResult> ExecuteAsync(
            string turnId,
            string toolName,
            JsonElement input,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolExecutionResult.Fail("NOT_SUPPORTED", "测试桩不执行工具"));
    }

    /// <summary>卡死的桥：POST 立即接受，轮询与 DELETE 一直挂起，只能靠取消超时收敛。</summary>
    private sealed class StallingBridgeHandler : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("不可达");
        }
    }

    /// <summary>
    /// 最小 HTTP 桥：<c>GET /health</c>、<c>POST /turns</c>、<c>GET /turns/{id}</c>、<c>DELETE /turns/{id}</c>。
    /// 用裸 TCP 实现，避免 HttpListener 的 URL ACL 与额外框架依赖。
    /// </summary>
    private sealed class FakeBridge : IAsyncDisposable
    {
        /// <summary>桥自称的 pid。</summary>
        public const int CorrectPid = 4242;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private FakeBridge(TcpListener listener, int pid, string usageSource)
        {
            _listener = listener;
            Pid = pid;
            UsageSource = usageSource;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = AcceptLoopAsync();
        }

        /// <summary>监听端口。</summary>
        public int Port { get; }

        /// <summary>回合响应里的 usage 来源标记。</summary>
        public string UsageSource { get; }

        /// <summary>启动桥。</summary>
        /// <param name="pid">桥自称的 pid。</param>
        /// <param name="usageSource">回合响应的 <c>usageSource</c>；默认 <c>provider</c>。</param>
        public static Task<FakeBridge> StartAsync(int pid, string usageSource = "provider")
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeBridge(listener, pid, usageSource));
        }

        /// <summary>桥自称的 pid。</summary>
        public int Pid { get; }

        /// <summary>停止并释放。</summary>
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // 预期路径：监听器已停止。
            }

            _stop.Dispose();
        }

        /// <summary>接受连接并逐条响应。</summary>
        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                using (client)
                {
                    await HandleAsync(client, _stop.Token);
                }
            }
        }

        /// <summary>读取请求行并返回固定响应。</summary>
        /// <param name="client">TCP 连接。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(requestLine))
            {
                return;
            }

            var parts = requestLine.Split(' ');
            var method = parts[0];
            var path = parts.Length > 1 ? parts[1] : "/";

            // 读掉请求头，避免对端等待；请求体不参与断言。
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
            {
            }

            var (status, body) = (method, path.Split('?')[0]) switch
            {
                ("GET", "/health") => (200, JsonSerializer.Serialize(new { ok = true, pid = Pid, model = "fake-model" })),
                ("POST", "/turns") => (202, JsonSerializer.Serialize(new { turnId = "turn-1" })),
                ("GET", var p) when p.StartsWith("/turns/", StringComparison.Ordinal) =>
                    (200, JsonSerializer.Serialize(new
                    {
                        state = "done",
                        text = "桥完成",
                        toolCalls = 2,
                        inputTokens = 11,
                        outputTokens = 7,
                        usageSource = UsageSource,
                        filesChanged = new[] { "a.txt" },
                    })),
                ("DELETE", var p) when p.StartsWith("/turns/", StringComparison.Ordinal) => (200, "{}"),
                _ => (404, "{}"),
            };

            var payload = Encoding.UTF8.GetBytes(body);
            var header = $"HTTP/1.1 {status} OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }
}
