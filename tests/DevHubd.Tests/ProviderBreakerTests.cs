using System.Net;
using DevHubd.Core.Data;
using DevHubd.Core.Models;
using DevHubd.Core.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 验收 2：错误分类与熔断隔离。配额/鉴权/无模型只影响对应 provider，瞬时失败按阈值打开。
/// </summary>
public sealed class ProviderBreakerTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteConnectionFactory _factory;
    private readonly ProviderStateStore _store;

    /// <summary>准备独立临时数据库。</summary>
    public ProviderBreakerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "devhub.db"));
        Migrator.MigrateAsync(_factory, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        _store = new ProviderStateStore(_factory);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }
    }

    /// <summary>HTTP 状态码按约定分类。</summary>
    [Theory]
    [InlineData(401, ModelErrorKinds.Auth)]
    [InlineData(403, ModelErrorKinds.Auth)]
    [InlineData(429, ModelErrorKinds.Quota)]
    [InlineData(404, ModelErrorKinds.NoModel)]
    [InlineData(408, ModelErrorKinds.Transient)]
    [InlineData(500, ModelErrorKinds.Transient)]
    [InlineData(503, ModelErrorKinds.Transient)]
    [InlineData(400, ModelErrorKinds.ModelError)]
    public void MapHttpError_MapsStatusToKind(int status, string expectedKind)
    {
        var error = OpenAiCompatibleModelClient.MapHttpError((HttpStatusCode)status, "{}");

        Assert.Equal(expectedKind, error.Kind);
    }

    /// <summary>429 且 error.code=insufficient_quota 时给出配额耗尽的稳定错误码。</summary>
    [Fact]
    public void MapHttpError_InsufficientQuota_UsesQuotaExhausted()
    {
        var error = OpenAiCompatibleModelClient.MapHttpError(
            HttpStatusCode.TooManyRequests,
            """{"error":{"code":"insufficient_quota"}}""");

        Assert.Equal(ModelErrorKinds.Quota, error.Kind);
        Assert.Equal("QUOTA_EXHAUSTED", error.Code);
    }

    /// <summary>配额失败只把 openai 置为 quota/open，vscode 保持 ready/closed。</summary>
    [Fact]
    public async Task RecordFailure_Quota_DoesNotAffectVsCodeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SaveAsync(BreakerPolicy.OnSuccess("vscode", DateTimeOffset.UtcNow), ct);

        await _store.RecordFailureAsync("openai", ModelErrorKinds.Quota, "配额耗尽。", ct);

        var openAi = await _store.GetAsync("openai", ct);
        Assert.NotNull(openAi);
        Assert.Equal(ProviderStates.Quota, openAi!.State);
        Assert.Equal(BreakerStates.Open, openAi.Breaker);

        var vsCode = await _store.GetAsync("vscode", ct);
        Assert.Equal(ProviderStates.Ready, vsCode!.State);
        Assert.Equal(BreakerStates.Closed, vsCode.Breaker);
    }

    /// <summary>瞬时失败连续 5 次才打开熔断，前 4 次保持 closed。</summary>
    [Fact]
    public async Task RecordFailure_Transient_OpensAtFifthFailureAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        for (var index = 1; index <= BreakerPolicy.TransientFailureThreshold; index++)
        {
            await _store.RecordFailureAsync("openai", ModelErrorKinds.Transient, $"第 {index} 次。", ct);
            var record = await _store.GetAsync("openai", ct);

            Assert.Equal(index, record!.ConsecutiveFailures);
            if (index < BreakerPolicy.TransientFailureThreshold)
            {
                Assert.Equal(BreakerStates.Closed, record.Breaker);
            }
            else
            {
                Assert.Equal(BreakerStates.Open, record.Breaker);
            }
        }
    }

    /// <summary>冷却期未到时禁止派发；冷却到期后转 half_open 放行一次探测。</summary>
    [Fact]
    public async Task IsDispatchAllowed_RespectsCooldownAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        await _store.SaveAsync(
            new ProviderStateRecord(
                "openai",
                ProviderStates.Quota,
                "配额耗尽。",
                BreakerStates.Open,
                ModelErrorKinds.Quota,
                now.ToString("O"),
                1,
                now.ToString("O")),
            ct);

        Assert.False(await _store.IsDispatchAllowedAsync("openai", ct));

        await _store.SaveAsync(
            new ProviderStateRecord(
                "openai",
                ProviderStates.Quota,
                "配额耗尽。",
                BreakerStates.Open,
                ModelErrorKinds.Quota,
                now.Subtract(BreakerPolicy.Cooldown).AddMinutes(-1).ToString("O"),
                1,
                now.ToString("O")),
            ct);

        Assert.True(await _store.IsDispatchAllowedAsync("openai", ct));
        Assert.Equal(BreakerStates.HalfOpen, (await _store.GetAsync("openai", ct))!.Breaker);
    }
}
