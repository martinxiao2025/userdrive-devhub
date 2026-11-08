using Microsoft.Extensions.AI;

namespace DevHubd.Core.Providers;

/// <summary>
/// 用量完整性跟踪：位于 <c>FunctionInvokingChatClient</c> 内侧，逐轮观察响应是否带真实 usage。
/// 只要任意一轮缺失，整回合的累加值就不可信，由执行器把用量置为 null（不改写框架的累加逻辑）。
/// </summary>
internal sealed class UsageTrackingChatClient : DelegatingChatClient
{
    private int _missingRounds;

    /// <summary>包装底层客户端。</summary>
    /// <param name="innerClient">底层客户端。</param>
    public UsageTrackingChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    /// <summary>是否每一轮都返回了可用 usage。</summary>
    public bool AllRoundsReportedUsage => Volatile.Read(ref _missingRounds) == 0;

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base
            .GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);

        if (!HasUsableCounts(response.Usage))
        {
            Interlocked.Increment(ref _missingRounds);
        }

        return response;
    }

    /// <summary>判断用量是否至少包含一个有效计数；空对象与全 0 都视为不可得。</summary>
    /// <param name="usage">用量。</param>
    internal static bool HasUsableCounts(UsageDetails? usage) =>
        usage is not null &&
        ((usage.InputTokenCount ?? 0) > 0 ||
         (usage.OutputTokenCount ?? 0) > 0 ||
         (usage.TotalTokenCount ?? 0) > 0);
}
