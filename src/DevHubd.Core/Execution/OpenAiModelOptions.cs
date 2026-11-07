namespace DevHubd.Core.Execution;

/// <summary>
/// OpenAI 兼容端点的用户级配置。<b>API key 不在此处</b>：只从环境变量读取，
/// 配置文件仅保存"变量名"（<see cref="ApiKeyEnvironmentVariable"/>），不保存明文。
/// </summary>
public sealed record OpenAiModelOptions
{
    /// <summary>兼容端点根地址，例如 <c>https://api.openai.com/v1</c>。</summary>
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";

    /// <summary>模型名。</summary>
    public string Model { get; init; } = "gpt-4.1-mini";

    /// <summary>保存 API key 的环境变量名。</summary>
    public string ApiKeyEnvironmentVariable { get; init; } = "DEVHUB_OPENAI_API_KEY";

    /// <summary>单次 HTTP 请求超时（秒）。</summary>
    public int RequestTimeoutSeconds { get; init; } = 120;

    /// <summary>收敛非法取值。</summary>
    public OpenAiModelOptions Normalize() => this with
    {
        BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? "https://api.openai.com/v1" : BaseUrl.TrimEnd('/'),
        Model = string.IsNullOrWhiteSpace(Model) ? "gpt-4.1-mini" : Model,
        ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable)
            ? "DEVHUB_OPENAI_API_KEY"
            : ApiKeyEnvironmentVariable,
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 5, 600),
    };
}
