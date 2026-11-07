namespace DevHubd.Core.Providers;

/// <summary>
/// API key 提供者。唯一实现从环境变量读取，配置文件不保存明文。
/// </summary>
public interface IApiKeyProvider
{
    /// <summary>环境变量名。</summary>
    string EnvironmentVariableName { get; }

    /// <summary>读取 API key；未配置时返回 null。</summary>
    string? GetApiKey();
}

/// <summary>
/// 环境变量实现：每次调用都重新读取，便于运维在不重启进程的情况下轮换 key。
/// 返回值只用于 HTTP 请求头，绝不进入日志、数据库或异常消息。
/// </summary>
public sealed class EnvironmentApiKeyProvider : IApiKeyProvider
{
    /// <summary>指定保存 key 的环境变量名。</summary>
    /// <param name="environmentVariableName">环境变量名。</param>
    public EnvironmentApiKeyProvider(string environmentVariableName)
    {
        EnvironmentVariableName = string.IsNullOrWhiteSpace(environmentVariableName)
            ? "DEVHUB_OPENAI_API_KEY"
            : environmentVariableName;
    }

    /// <inheritdoc />
    public string EnvironmentVariableName { get; }

    /// <inheritdoc />
    public string? GetApiKey()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
