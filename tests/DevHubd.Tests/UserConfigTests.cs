using System.Text;
using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Providers;
using DevHubd.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
        WithClearedEnvironment(
            new[]
            {
                "DEVHUB__Model__OpenAi__BaseUrl",
                "DEVHUB__Model__OpenAi__Model",
                "DEVHUB__Model__OpenAi__ApiKeyEnvironmentVariable",
            },
            () =>
            {
                var config = DevHubUserConfig.Load(Path.Combine(_directory, "missing.json"));

                Assert.Empty(config.Problems);
                Assert.Equal("https://api.openai.com/v1", config.OpenAi.BaseUrl);
                Assert.Equal("DEVHUB_OPENAI_API_KEY", config.OpenAi.ApiKeyEnvironmentVariable);
            });
    }

    /// <summary>文件缺失时 DEVHUB__... 环境变量覆盖仍然生效（配置层叠不依赖文件存在）。</summary>
    [Fact]
    public void Load_MissingFile_StillAppliesEnvironmentOverrides()
    {
        const string name = "DEVHUB__Model__OpenAi__RequestTimeoutSeconds";
        WithEnvironment(name, "77", () =>
        {
            var config = DevHubUserConfig.Load(Path.Combine(_directory, "missing.json"));

            Assert.Equal(77, config.OpenAi.RequestTimeoutSeconds);
        });
    }

    /// <summary>在清除指定环境变量的状态下执行断言，结束后恢复原值。</summary>
    /// <param name="names">环境变量名。</param>
    /// <param name="assertion">断言逻辑。</param>
    private static void WithClearedEnvironment(string[] names, Action assertion)
    {
        var saved = names
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .ToArray();
        try
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            assertion();
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>在设置指定环境变量的状态下执行断言，结束后恢复原值。</summary>
    /// <param name="name">环境变量名。</param>
    /// <param name="value">临时值。</param>
    /// <param name="assertion">断言逻辑。</param>
    private static void WithEnvironment(string name, string value, Action assertion)
    {
        var saved = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            assertion();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, saved);
        }
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

    /// <summary><c>Execution:ScheduledProvider</c> 绑定到执行配置；大小写不敏感。</summary>
    [Fact]
    public void Load_ScheduledProviderVscode_Binds()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(
            path,
            """
            { "execution": { "scheduledProvider": "VSCODE" } }
            """,
            new UTF8Encoding(false));

        var config = DevHubUserConfig.Load(path);

        Assert.Empty(config.Problems);
        Assert.Equal("vscode", config.Execution.ScheduledProvider);
    }

    /// <summary>非法 provider 回退 openai，并写入配置问题（启动时以 Warning 记录），不静默吞掉。</summary>
    [Fact]
    public void Load_InvalidScheduledProvider_FallsBackAndReports()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(
            path,
            """
            { "execution": { "scheduledProvider": "gemini" } }
            """,
            new UTF8Encoding(false));

        var config = DevHubUserConfig.Load(path);

        Assert.Equal("openai", config.Execution.ScheduledProvider);
        Assert.Single(config.Problems);
        Assert.Contains("ScheduledProvider", config.Problems[0], StringComparison.Ordinal);
        Assert.Contains("gemini", config.Problems[0], StringComparison.Ordinal);
    }

    /// <summary>占位 key 在探测阶段判为 auth 并给出明确 detail，不阻断启动。</summary>
    [Fact]
    public async Task ProbeAsync_PlaceholderApiKey_ReportsAuthAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"DEVHUB_TEST_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "CHANGE_ME");
        try
        {
            using var http = new HttpClient();
            var client = new OpenAiCompatibleModelClient(
                new TestHttpClientFactory(http),
                new OpenAiModelOptions { ApiKeyEnvironmentVariable = name },
                new EnvironmentApiKeyProvider(name),
                NullLoggerFactory.Instance,
                NullLogger<OpenAiCompatibleModelClient>.Instance);

            var health = await client.ProbeAsync(ct);

            Assert.False(health.Available);
            Assert.Equal(ProviderStates.Auth, health.State);
            Assert.Contains("占位", health.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
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
