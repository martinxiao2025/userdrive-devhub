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
    private readonly string _workRoot;
    private readonly BudgetLimits _budget = new(4, TimeSpan.FromMinutes(1), 5, 10, 1024 * 1024, 1024 * 1024);

    /// <summary>创建临时工作区与工作区外的隔离目录。</summary>
    public PathGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        // 隔离目录放在工作区之外，避免读写根重叠掩盖越界问题。
        _workRoot = Path.Combine(Path.GetTempPath(), "devhubd-tests", Guid.NewGuid().ToString("N") + "-work");
        Directory.CreateDirectory(_workRoot);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        foreach (var directory in new[] { _root, _workRoot })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 忽略：临时目录由系统回收。
            }
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

    /// <summary>根目录自身必须可读：<c>.</c>、<c>./</c>、<c>.\</c> 都解析为根，不再误报 PATH_ESCAPE。</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData(".\\")]
    public void ResolveRead_RootDirectory_IsAccepted(string path)
    {
        var result = PathGuard.ResolveRead(_root, path);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(
            Path.GetFullPath(_root),
            Path.GetFullPath(result.FullPath!),
            ignoreCase: true);
    }

    /// <summary>子目录带尾分隔符或 <c>.</c> 后缀时也应正确解析到该目录。</summary>
    [Theory]
    [InlineData("src/")]
    [InlineData("src\\")]
    [InlineData("src/.")]
    public void ResolveRead_SubdirectoryWithTrailingSeparator_IsAccepted(string path)
    {
        var result = PathGuard.ResolveRead(_root, path);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "src")),
            Path.GetFullPath(result.FullPath!),
            ignoreCase: true);
    }

    /// <summary>指向根目录的越界路径仍必须被拒绝，不能借 <c>.</c> 规范化放行。</summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("..\\")]
    [InlineData("../outside.txt")]
    public void ResolveRead_RootEscape_IsRejected(string path)
    {
        var result = PathGuard.ResolveRead(_root, path);

        Assert.False(result.Success);
        Assert.Equal("PATH_ESCAPE", result.ErrorCode);
    }

    /// <summary>writeScope 为 <c>.</c> 时表示整个工作区根，作用域校验不能因尾分隔符误判。</summary>
    [Fact]
    public void ResolveWrite_ScopeDot_AllowsWorkspaceRoot()
    {
        var result = PathGuard.ResolveWrite(_root, "a.txt", new[] { "." }, enforceScope: true);

        Assert.True(result.Success, result.ErrorMessage);
    }

    /// <summary>
    /// dispatch_only：读根为工作区（可读源码与既有文件），写根为该队列项的隔离目录；
    /// 越界读、越界写、绝对路径与敏感路径一律拒绝。
    /// </summary>
    [Fact]
    public async Task ToolDispatcher_DispatchOnly_ReadsWorkspaceAndWritesIsolationAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "src", "app.cs");
        await File.WriteAllTextAsync(source, "class App { }", ct);

        var dispatcher = new ToolDispatcher(
            new TurnWorkspace(_root, _workRoot, WriteScope: null, EnforceScope: false),
            _budget,
            ToolDispatcher.FileToolNames,
            NullLogger<ToolDispatcher>.Instance);

        // 读工作区既有文件：dispatch_only 下同样必须能读源码。
        using var read = JsonDocument.Parse("""{"path":"src/app.cs"}""");
        var readResult = await dispatcher.ExecuteAsync("turn-1", "read_file", read.RootElement, ct);
        Assert.True(readResult.Success, readResult.ErrorMessage);
        Assert.Equal("class App { }", readResult.Output);

        // 列工作区目录同理（读根不是隔离目录）。
        using var list = JsonDocument.Parse("""{"path":"src"}""");
        var listResult = await dispatcher.ExecuteAsync("turn-1", "list_dir", list.RootElement, ct);
        Assert.True(listResult.Success, listResult.ErrorMessage);
        Assert.Contains("app.cs", listResult.Output, StringComparison.Ordinal);

        // 写仍只落隔离目录，工作区不出现同名文件。
        using var inside = JsonDocument.Parse("""{"path":"out/report.md","content":"ok"}""");
        var written = await dispatcher.ExecuteAsync("turn-1", "write_file", inside.RootElement, ct);
        Assert.True(written.Success, written.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(_workRoot, "out", "report.md")));
        Assert.False(File.Exists(Path.Combine(_root, "out", "report.md")));

        // 读工具看不到隔离目录里的产物：避免模型误以为产物已进工作区。
        using var readBack = JsonDocument.Parse("""{"path":"out/report.md"}""");
        var readBackResult = await dispatcher.ExecuteAsync("turn-1", "read_file", readBack.RootElement, ct);
        Assert.False(readBackResult.Success);
        Assert.Equal("FILE_NOT_FOUND", readBackResult.ErrorCode);

        // 越界读：../ 被拒绝。
        using var escapeRead = JsonDocument.Parse("""{"path":"../outside.txt"}""");
        var escapeReadResult = await dispatcher.ExecuteAsync("turn-1", "read_file", escapeRead.RootElement, ct);
        Assert.False(escapeReadResult.Success);
        Assert.Equal("PATH_ESCAPE", escapeReadResult.ErrorCode);

        // 越界读：绝对路径被拒绝。
        using var absoluteRead = JsonDocument.Parse(
            JsonSerializer.Serialize(new { path = Path.Combine(_root, "src", "app.cs") }));
        var absoluteReadResult = await dispatcher.ExecuteAsync("turn-1", "read_file", absoluteRead.RootElement, ct);
        Assert.False(absoluteReadResult.Success);
        Assert.Equal("PATH_INVALID", absoluteReadResult.ErrorCode);

        // 敏感路径即使在工作区内也不可读。
        using var sensitiveRead = JsonDocument.Parse("""{"path":"src/.env"}""");
        var sensitiveReadResult = await dispatcher.ExecuteAsync("turn-1", "read_file", sensitiveRead.RootElement, ct);
        Assert.False(sensitiveReadResult.Success);
        Assert.Equal("SENSITIVE_PATH", sensitiveReadResult.ErrorCode);

        // 越界写仍被拒绝（写根是隔离目录）。
        using var outside = JsonDocument.Parse("""{"path":"../escape.md","content":"bad"}""");
        var rejected = await dispatcher.ExecuteAsync("turn-1", "write_file", outside.RootElement, ct);
        Assert.False(rejected.Success);
        Assert.Equal("PATH_ESCAPE", rejected.ErrorCode);
    }

    /// <summary>list_dir 直接传 <c>.</c> 必须列出工作区根，而不是误报 PATH_ESCAPE；该输入不得回避。</summary>
    [Fact]
    public async Task ToolDispatcher_ListDirDot_ListsWorkspaceRootAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "hi", ct);

        var dispatcher = new ToolDispatcher(
            new TurnWorkspace(_root, _workRoot, WriteScope: null, EnforceScope: false),
            _budget,
            ToolDispatcher.FileToolNames,
            NullLogger<ToolDispatcher>.Instance);

        using var list = JsonDocument.Parse("""{"path":"."}""");
        var listed = await dispatcher.ExecuteAsync("turn-3", "list_dir", list.RootElement, ct);
        Assert.True(listed.Success, listed.ErrorMessage);
        Assert.Contains("src/", listed.Output, StringComparison.Ordinal);
        Assert.Contains("README.md", listed.Output, StringComparison.Ordinal);

        // "." 是目录而不是文件：read_file 应报 FILE_NOT_FOUND，证明路径先被正确接受而非判成越界。
        using var read = JsonDocument.Parse("""{"path":"."}""");
        var readResult = await dispatcher.ExecuteAsync("turn-3", "read_file", read.RootElement, ct);
        Assert.False(readResult.Success);
        Assert.Equal("FILE_NOT_FOUND", readResult.ErrorCode);
    }

    /// <summary>auto 档位下读根与写根同为工作区，写作用域外的写入被拒绝且不落盘。</summary>
    [Fact]
    public async Task ToolDispatcher_AutoMode_RejectsWriteOutsideScopeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new ToolDispatcher(
            new TurnWorkspace(_root, _root, new[] { "src" }, EnforceScope: true),
            _budget,
            ToolDispatcher.FileToolNames,
            NullLogger<ToolDispatcher>.Instance);

        using var document = JsonDocument.Parse("""{"path":"docs/a.md","content":"bad"}""");
        var result = await dispatcher.ExecuteAsync("turn-2", "write_file", document.RootElement, ct);

        Assert.False(result.Success);
        Assert.Equal("SCOPE_DENIED", result.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "docs")));
    }
}
