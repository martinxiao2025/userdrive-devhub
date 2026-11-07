using DevHubd.Core.Models;

namespace DevHubd.Tests;

/// <summary>
/// 测试用模型客户端。<b>只验证执行器编排语义，不证明真实模型可用</b>：
/// 真实 OpenAI 调用需要凭据，缺少凭据时相关能力在验收记录中标注"未验证"。
/// </summary>
internal sealed class FakeModelClient : IModelClient
{
    private readonly Func<TurnRequest, IToolDispatcher, CancellationToken, Task<TurnResult>> _handler;
    private int _active;
    private int _maxActive;
    private int _calls;

    /// <summary>构造假客户端。</summary>
    /// <param name="provider">provider 标识。</param>
    /// <param name="handler">回合处理函数。</param>
    public FakeModelClient(
        string provider,
        Func<TurnRequest, IToolDispatcher, CancellationToken, Task<TurnResult>> handler)
    {
        Provider = provider;
        _handler = handler;
    }

    /// <inheritdoc />
    public string Provider { get; }

    /// <summary>观察到的最大并发回合数（用于验证 MaxParallel=1 严格串行）。</summary>
    public int MaxConcurrency => Volatile.Read(ref _maxActive);

    /// <summary>被调用次数。</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc />
    public Task<ModelHealth> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ModelHealth(true, "ready", null, "fake"));

    /// <inheritdoc />
    public async Task<TurnResult> RunTurnAsync(
        TurnRequest request,
        IToolDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        var active = Interlocked.Increment(ref _active);
        UpdateMax(active);

        try
        {
            return await _handler(request, dispatcher, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>更新观测到的最大并发。</summary>
    /// <param name="active">当前并发。</param>
    private void UpdateMax(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maxActive);
            if (active <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maxActive, active, current) == current)
            {
                return;
            }
        }
    }
}
