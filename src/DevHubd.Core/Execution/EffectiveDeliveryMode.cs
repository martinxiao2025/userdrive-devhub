namespace DevHubd.Core.Execution;

/// <summary>
/// 运行时交付档位单例：进程内唯一可读来源。
/// 启动时初始值取用户配置 <c>Execution:DeliveryMode</c>，随后由 <c>scheduler_state.mode</c> 覆盖；
/// <c>scheduler_control set_mode</c> 写库成功后同步更新本单例，保证执行、健康检查与资源上报口径一致。
/// </summary>
public sealed class EffectiveDeliveryMode
{
    private string _value;

    /// <summary>用初始档位构造单例；非法值收敛为 <c>dispatch_only</c>。</summary>
    /// <param name="initial">初始档位（通常来自用户配置）。</param>
    public EffectiveDeliveryMode(string initial)
    {
        _value = Normalize(initial);
    }

    /// <summary>当前生效档位：<c>auto</c> 或 <c>dispatch_only</c>。</summary>
    public string Value => Volatile.Read(ref _value);

    /// <summary>更新生效档位；只接受 <c>auto</c> / <c>dispatch_only</c>，其余收敛为 <c>dispatch_only</c>。</summary>
    /// <param name="mode">目标档位。</param>
    public void Set(string mode) => Volatile.Write(ref _value, Normalize(mode));

    /// <summary>把任意输入归一化为两个合法档位之一。</summary>
    /// <param name="mode">输入档位。</param>
    private static string Normalize(string mode) =>
        string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : "dispatch_only";
}
