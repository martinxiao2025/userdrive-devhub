using System.Threading.Channels;

namespace DevHubd.Scheduling;

/// <summary>
/// 调度唤醒信号：<c>scheduler_control scan_now</c> 通过它打断后台等待，让下一次 tick 立即执行。
/// 容量固定为 1 且写满时丢弃：多次唤醒只保留一次，避免积压成连续扫描。
/// </summary>
public sealed class SchedulerWakeSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <summary>请求一次立即扫描；非阻塞，重复请求会被合并。</summary>
    public void Request() => _channel.Writer.TryWrite(0);

    /// <summary>等待下一次唤醒。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task WaitAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken).AsTask();
}
