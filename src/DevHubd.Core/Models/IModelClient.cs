namespace DevHubd.Core.Models;

/// <summary>
/// 模型客户端抽象。生产实现只有两个：<c>openai</c>（BYOK 直连）与 <c>vscode</c>（扩展桥）。
/// 测试使用 <c>FakeModelClient</c>，不得在生产路径引入第三个模型通道。
/// </summary>
public interface IModelClient
{
    /// <summary>提供方标识：openai | vscode。</summary>
    string Provider { get; }

    /// <summary>探测可用性；不抛异常。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ModelHealth> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>执行一个回合；失败以返回值表达，不因业务错误抛异常。</summary>
    /// <param name="request">回合请求。</param>
    /// <param name="dispatcher">工具执行器（openai 路径由 C# 驱动工具循环）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TurnResult> RunTurnAsync(
        TurnRequest request,
        IToolDispatcher dispatcher,
        CancellationToken cancellationToken);
}
