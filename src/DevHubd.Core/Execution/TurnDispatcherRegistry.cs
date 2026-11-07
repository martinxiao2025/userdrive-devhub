using System.Collections.Concurrent;
using DevHubd.Core.Models;

namespace DevHubd.Core.Execution;

/// <summary>
/// 活跃回合的 <see cref="IToolDispatcher"/> 注册表。
/// VS Code 扩展在对 <c>vscode.lm</c> 回合内的工具调用会回调 C# 的 <c>/internal/tools/call</c>，
/// 该端点据此找到"这个 turnId 该用哪个工作区与预算"的派发器。
/// 只存在于进程内存：进程重启后旧回合自然失效，端点返回 <c>UNKNOWN_TURN</c>。
/// </summary>
public sealed class TurnDispatcherRegistry
{
    private readonly ConcurrentDictionary<string, IToolDispatcher> _dispatchers = new(StringComparer.Ordinal);

    /// <summary>登记一个活跃回合。</summary>
    /// <param name="turnId">回合 id。</param>
    /// <param name="dispatcher">该回合的工具派发器。</param>
    public void Register(string turnId, IToolDispatcher dispatcher) => _dispatchers[turnId] = dispatcher;

    /// <summary>注销回合；不存在时无副作用。</summary>
    /// <param name="turnId">回合 id。</param>
    public void Unregister(string turnId) => _dispatchers.TryRemove(turnId, out _);

    /// <summary>按 turnId 取派发器。</summary>
    /// <param name="turnId">回合 id。</param>
    /// <param name="dispatcher">派发器。</param>
    public bool TryGet(string turnId, out IToolDispatcher? dispatcher) =>
        _dispatchers.TryGetValue(turnId, out dispatcher);

    /// <summary>当前活跃回合数（状态观测与测试用）。</summary>
    public int Count => _dispatchers.Count;
}
