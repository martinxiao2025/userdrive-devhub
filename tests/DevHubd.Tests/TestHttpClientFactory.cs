using System.Net.Http;

namespace DevHubd.Tests;

/// <summary>
/// 测试用 <see cref="IHttpClientFactory"/>：返回调用方准备好的 HttpClient，
/// 并补一个占位 BaseAddress，避免相对路径请求在单测里失败。
/// </summary>
internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    /// <summary>包装给定客户端。</summary>
    /// <param name="client">测试构造的 HttpClient（可带自定义 handler）。</param>
    public TestHttpClientFactory(HttpClient client)
    {
        _client = client;
        // 生产环境由具名客户端配置 BaseAddress；单测只关心请求内容，用占位地址即可。
        _client.BaseAddress ??= new Uri("https://example.invalid/v1/");
    }

    /// <inheritdoc />
    public HttpClient CreateClient(string name) => _client;
}
