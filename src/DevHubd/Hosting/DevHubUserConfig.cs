using System.Text.Json;
using DevHubd.Core.Execution;
using Microsoft.Extensions.Configuration;

namespace DevHubd.Hosting;

/// <summary>
/// 用户级配置加载结果。
/// </summary>
/// <param name="OpenAi">OpenAI 兼容端点配置（不含 key）。</param>
/// <param name="Execution">执行参数。</param>
/// <param name="Problems">配置问题（启动时以 Warning 记录）。</param>
public sealed record DevHubUserConfig(
    OpenAiModelOptions OpenAi,
    ExecutionOptions Execution,
    IReadOnlyList<string> Problems)
{
    /// <summary>配置文件缺失时使用的默认配置。</summary>
    public static DevHubUserConfig Defaults { get; } = new(
        new OpenAiModelOptions(),
        new ExecutionOptions(),
        Array.Empty<string>());

    /// <summary>
    /// 从 <c>%LOCALAPPDATA%\userdrive-devhub\config.json</c> 加载配置。
    /// 配置键采用 <c>Sections:Keys</c> 形式（<c>Model:OpenAi:BaseUrl</c>、<c>Execution:MaxParallel</c>），
    /// 可用环境变量 <c>DEVHUB__Model__OpenAi__BaseUrl</c> 覆盖。
    /// 配置文件中若出现 <c>apiKey</c> 一类的明文字段，会被拒绝并记录问题，key 始终只从环境变量读取。
    /// </summary>
    /// <param name="filePath">配置文件绝对路径。</param>
    public static DevHubUserConfig Load(string filePath)
    {
        var problems = new List<string>();

        if (!File.Exists(filePath))
        {
            return Defaults;
        }

        OpenAiModelOptions openAi;
        ExecutionOptions execution;
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(filePath, optional: true, reloadOnChange: false)
                .AddEnvironmentVariables("DEVHUB__")
                .Build();

            openAi = configuration.GetSection("Model:OpenAi").Get<OpenAiModelOptions>() ?? new OpenAiModelOptions();
            execution = configuration.GetSection("Execution").Get<ExecutionOptions>() ?? new ExecutionOptions();
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or JsonException)
        {
            // 配置损坏不阻断启动：回退默认值并把问题暴露给宿主日志。
            return new DevHubUserConfig(
                new OpenAiModelOptions(),
                new ExecutionOptions(),
                new[] { $"配置文件无法解析，已回退默认值：{exception.Message}" });
        }

        if (ContainsPlaintextApiKey(filePath))
        {
            problems.Add(
                $"配置文件包含 apiKey 明文，已忽略；请删除该字段并改用环境变量 {openAi.ApiKeyEnvironmentVariable}。");
        }

        return new DevHubUserConfig(openAi.Normalize(), execution.Normalize(), problems);
    }

    /// <summary>扫描 JSON 中是否存在 <c>apiKey</c> 一类的明文字段（值为非空字符串）。</summary>
    /// <param name="filePath">配置文件路径。</param>
    private static bool ContainsPlaintextApiKey(string filePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            return Scan(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // 解析失败时按现有默认配置继续，问题在绑定阶段由框架自行报告。
            return false;
        }
    }

    /// <summary>递归扫描对象属性名。</summary>
    /// <param name="element">JSON 节点。</param>
    private static bool Scan(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsApiKeyName(property.Name) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        return true;
                    }

                    if (Scan(property.Value))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (Scan(item))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>判断属性名是否为 API key 明文。</summary>
    /// <param name="name">属性名。</param>
    private static bool IsApiKeyName(string name) =>
        name.Equals("apiKey", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("openAiApiKey", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("api_key", StringComparison.OrdinalIgnoreCase);
}
