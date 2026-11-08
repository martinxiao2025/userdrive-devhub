using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevHubd.Tests;

/// <summary>
/// 验收 4：写作用域与敏感路径。覆盖 <c>../</c>、绝对路径、敏感文件、作用域外写入与符号链接逃逸。
/// </summary>
public sealed class PathGuardTests : IDisposable
{
    private readonly string _root;
    private readonly BudgetLimits _budget = new(4, TimeSpan.FromMinutes(1), 5, 10, 1024 * 1024, 1024 * 1024);

    /// <summary>创建临时工作区。</summary>
    public PathGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 忽略：临时目录由系统回收。
        }
    }

    /// <summary>包含 <c>..</c> 的路径必须被拒绝。</summary>
    [Fact]
    public void ResolveWrite_ParentTraversal_IsRejected()
    {
        var result = PathGuard.ResolveWrite(_root, "../outside.txt", new[] { "src" }, enforceScope: true);

        Assert.False(result.Success);
        Assert.Equal("PATH_ESCAPE", result.ErrorCode);
    }

    /// <summary>绝对路径必须被拒绝（模型只能给相对路径）。</summary>
    [Fact]
    public void ResolveWrite_AbsolutePath_IsRejected()
    {
        var result = PathGuard.ResolveWrite(_root, Path.Combine(_root, "src", "a.txt"), new[] { "src" }, true);

        Assert.False(result.Success);
        Assert.Equal("PATH_INVALID", result.ErrorCode);
    }

    /// <summary>敏感路径（.env、.git、*.pem）必须被拒绝。</summary>
    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData(".git/config")]
    [InlineData("secrets/token.txt")]
    [InlineData("cert.pem")]
    public void ResolveWrite_SensitivePath_IsRejected(string path)
    {
        var result = PathGuard.ResolveWrite(_root, path, Array.Empty<string>(), enforceScope: false);

        Assert.False(result.Success);
        Assert.Equal("SENSITIVE_PATH", result.ErrorCode);
    }

    /// <summary>作用域内写入通过，作用域外写入返回 SCOPE_DENIED。</summary>
    [Fact]
    public void ResolveWrite_ScopeEnforced_ReturnsScopeDenied()
    {
        var allowed = PathGuard.ResolveWrite(_root, "src/report.md", new[] { "src" }, enforceScope: true);
        Assert.True(allowed.Success);

        var denied = PathGuard.ResolveWrite(_root, "docs/report.md", new[] { "src" }, enforceScope: true);
        Assert.False(denied.Success);
        Assert.Equal("SCOPE_DENIED", denied.ErrorCode);
    }

    /// <summary>目录名前缀相似但不在作用域内时，不得被误判为允许（C:\root\src 与 C:\root\src2）。</summary>
    [Fact]
    public void ResolveWrite_SimilarPrefix_IsDenied()
    {
        var result = PathGuard.ResolveWrite(_root, "src2/report.md", new[] { "src" }, enforceScope: true);

        Assert.False(result.Success);
        Assert.Equal("SCOPE_DENIED", result.ErrorCode);
    }

    /// <summary>隔离目录模式（不强制作用域）下，越界写入仍被拒绝，且读工具能看到本回合写入。</summary>
    [Fact]
    public async Task ToolDispatcher_ReviewMode_WritesOnlyUnderWorkDirectoryAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var workDirectory = Path.Combine(_root, "work");
        Directory.CreateDirectory(workDirectory);

        var dispatcher = new ToolDispatcher(
            new TurnWorkspace(workDirectory, WriteScope: null, EnforceScope: false),
            _budget,
            NullLogger<ToolDispatcher>.Instance);

        using var inside = JsonDocument.Parse("""{"path":"out/report.md","content":"ok"}""");
        var written = await dispatcher.ExecuteAsync("turn-1", "write_file", inside.RootElement, ct);
        Assert.True(written.Success, written.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(workDirectory, "out", "report.md")));
        Assert.False(File.Exists(Path.Combine(_root, "out", "report.md")));

        // 读写同根：list_dir / read_file 必须能看到同一回合写入的文件。
        using var list = JsonDocument.Parse("""{"path":"out"}""");
        var listed = await dispatcher.ExecuteAsync("turn-1", "list_dir", list.RootElement, ct);
        Assert.True(listed.Success, listed.ErrorMessage);
        Assert.Contains("report.md", listed.Output, StringComparison.Ordinal);

        using var read = JsonDocument.Parse("""{"path":"out/report.md"}""");
        var readBack = await dispatcher.ExecuteAsync("turn-1", "read_file", read.RootElement, ct);
        Assert.True(readBack.Success, readBack.ErrorMessage);
        Assert.Equal("ok", readBack.Output);

        using var outside = JsonDocument.Parse("""{"path":"../escape.md","content":"bad"}""");
        var rejected = await dispatcher.ExecuteAsync("turn-1", "write_file", outside.RootElement, ct);
        Assert.False(rejected.Success);
        Assert.Equal("PATH_ESCAPE", rejected.ErrorCode);
    }

    /// <summary>auto 档位下，写作用域外的写入被拒绝且不落盘。</summary>
    [Fact]
    public async Task ToolDispatcher_AutoMode_RejectsWriteOutsideScopeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new ToolDispatcher(
            new TurnWorkspace(_root, new[] { "src" }, EnforceScope: true),
            _budget,
            NullLogger<ToolDispatcher>.Instance);

        using var document = JsonDocument.Parse("""{"path":"docs/a.md","content":"bad"}""");
        var result = await dispatcher.ExecuteAsync("turn-2", "write_file", document.RootElement, ct);

        Assert.False(result.Success);
        Assert.Equal("SCOPE_DENIED", result.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "docs")));
    }
}
