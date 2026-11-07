using DevHubd.Core.Models;

namespace DevHubd.Core.Execution;

/// <summary>
/// provider → 模型客户端的注册表。P2 只有 openai；vscode 客户端在 P3 由扩展桥提供。
/// 缺省 provider 视为不可用，执行器按 provider 隔离处理，不做跨通道静默兜底。
/// </summary>
public sealed class ModelClientRegistry
{
    private readonly IReadOnlyDictionary<string, IModelClient> _clients;

    /// <summary>注入全部 <see cref="IModelClient"/> 实现。</summary>
    /// <param name="clients">模型客户端集合。</param>
    public ModelClientRegistry(IEnumerable<IModelClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Provider, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>按 provider 取客户端；未注册返回 null。</summary>
    /// <param name="provider">provider 标识。</param>
    public IModelClient? Get(string provider) =>
        _clients.GetValueOrDefault(provider);

    /// <summary>已注册的 provider 列表。</summary>
    public IReadOnlyList<string> Providers => _clients.Keys.ToArray();
}
