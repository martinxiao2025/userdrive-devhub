using System.Text;
using System.Text.Json;
using DevHubd.Core;
using DevHubd.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 日志门禁测试：关键路径必须产出可断言的级别、EventId 与结构化字段；
/// 日志格式与级别必须由配置控制，而不是写死在代码里。
/// 与 <see cref="StartupExitCodeTests"/> 共用机器级互斥体，必须放在同一 collection 串行执行。
/// </summary>
[Collection("single-instance")]
public sealed class LoggingTests
{
    /// <summary>第二实例必须写 Error 级、EventId 1002 的日志，并带锁文件路径与排查提示。</summary>
    [Fact]
    public void SecondInstance_WritesErrorLogWithLockPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var paths = new DevHubPaths(directory);
            using var first = new SingleInstanceGuard(NullLogger<SingleInstanceGuard>.Instance);

            if (!first.TryAcquire(paths, out _))
            {
                Assert.Skip("本机已有 devhubd 实例在运行，无法构造第二实例场景。");
            }

            var logger = new FakeLogger<SingleInstanceGuard>();
            using var second = new SingleInstanceGuard(logger);

            Assert.False(second.TryAcquire(paths, out var hint));

            var record = Assert.Single(logger.Collector.GetSnapshot());
            Assert.Equal(LogLevel.Error, record.Level);
            Assert.Equal(1002, record.Id);
            Assert.Contains(paths.RuntimeLockPath, record.Message);
            Assert.Contains("Get-Process -Name devhubd", hint);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录由系统回收。
            }
        }
    }

    /// <summary>appsettings.json 必须保留控制台格式开关与日志级别配置，否则 JSON 输出与级别覆盖失效。</summary>
    [Fact]
    public void AppSettings_KeepsConsoleFormatterAndLogLevel()
    {
        var settingsPath = FindRepositoryFile(Path.Combine("src", "DevHubd", "appsettings.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath, Encoding.UTF8));
        var logging = document.RootElement.GetProperty("Logging");

        Assert.Equal("simple", logging.GetProperty("Console").GetProperty("FormatterName").GetString());
        Assert.True(logging.GetProperty("LogLevel").TryGetProperty("Default", out var defaultLevel));
        Assert.Equal("Information", defaultLevel.GetString());
    }

    /// <summary>从测试输出目录向上找到仓库根（以 DevHubd.sln 为标志），定位仓库内文件。</summary>
    /// <param name="relativePath">相对仓库根的路径。</param>
    /// <returns>文件的绝对路径。</returns>
    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var solution = Path.Combine(directory.FullName, "DevHubd.sln");
            if (File.Exists(solution))
            {
                return Path.Combine(directory.FullName, relativePath);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"未能从 {AppContext.BaseDirectory} 向上找到 DevHubd.sln。");
    }
}
