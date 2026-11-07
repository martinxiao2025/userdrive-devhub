using System.Text;
using System.Text.Json;
using DevHubd.Core.Providers;
using DevHubd.Hosting;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 用户级配置：baseUrl/model 可落配置文件，API key 只从环境变量读取，配置文件里的明文字段被拒绝。
/// </summary>
public sealed class UserConfigTests : IDisposable
{
    private readonly string _directory;

    /// <summary>创建临时目录。</summary>
    public UserConfigTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }
    }

    /// <summary>配置文件缺失时使用默认值，不产生问题。</summary>
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var config = DevHubUserConfig.Load(Path.Combine(_directory, "missing.json"));

        Assert.Empty(config.Problems);
        Assert.Equal("https://api.openai.com/v1", config.OpenAi.BaseUrl);
        Assert.Equal("DEVHUB_OPENAI_API_KEY", config.OpenAi.ApiKeyEnvironmentVariable);
    }

    /// <summary>camelCase 配置文件按 <c>Model:OpenAi</c> / <c>Execution</c> 键绑定。</summary>
    [Fact]
    public void Load_CamelCaseConfig_BindsSections()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(
            path,
            """
            {
              "model": { "openAi": { "baseUrl": "https://example.test/v1", "model": "custom-model" } },
              "execution": { "maxParallel": 2, "maxDurationSeconds": 42 }
            }
            """,
            new UTF8Encoding(false));

        var config = DevHubUserConfig.Load(path);

        Assert.Empty(config.Problems);
        Assert.Equal("https://example.test/v1", config.OpenAi.BaseUrl);
        Assert.Equal("custom-model", config.OpenAi.Model);
        Assert.Equal(2, config.Execution.MaxParallel);
        Assert.Equal(42, config.Execution.MaxDurationSeconds);
    }

    /// <summary>配置文件出现 apiKey 明文时记录问题，key 仍只从环境变量读取。</summary>
    [Fact]
    public void Load_PlaintextApiKey_IsReportedAndIgnored()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(
            path,
            """
            {
              "model": { "openAi": { "baseUrl": "https://example.test/v1", "apiKey": "sk-should-not-be-here" } }
            }
            """,
            new UTF8Encoding(false));

        var config = DevHubUserConfig.Load(path);

        Assert.Single(config.Problems);
        Assert.Contains("apiKey", config.Problems[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://example.test/v1", config.OpenAi.BaseUrl);
    }

    /// <summary>环境变量存在时被读取，删除后返回 null。</summary>
    [Fact]
    public void EnvironmentApiKeyProvider_ReadsOnlyEnvironment()
    {
        var name = $"DEVHUB_TEST_KEY_{Guid.NewGuid():N}";
        var provider = new EnvironmentApiKeyProvider(name);

        Assert.Null(provider.GetApiKey());

        try
        {
            Environment.SetEnvironmentVariable(name, "test-key-value");
            Assert.Equal("test-key-value", provider.GetApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Assert.Null(provider.GetApiKey());
    }

    /// <summary>配置文件不是合法 JSON 时不抛异常，按默认值继续。</summary>
    [Fact]
    public void Load_InvalidJson_FallsBackToDefaults()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, "{ not json", new UTF8Encoding(false));

        var config = DevHubUserConfig.Load(path);

        Assert.Equal("https://api.openai.com/v1", config.OpenAi.BaseUrl);
    }

    /// <summary>保证配置对象可序列化（文档与运维脚本依赖）。</summary>
    [Fact]
    public void Defaults_AreSerializable()
    {
        var json = JsonSerializer.Serialize(DevHubUserConfig.Defaults.OpenAi);

        Assert.Contains("baseUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", json, StringComparison.Ordinal);
    }
}
