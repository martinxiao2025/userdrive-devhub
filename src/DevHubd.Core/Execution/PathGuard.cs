namespace DevHubd.Core.Execution;

/// <summary>路径校验结果。成功时 <see cref="FullPath"/> 为规范化后的绝对路径。</summary>
/// <param name="Success">是否通过校验。</param>
/// <param name="FullPath">规范化后的绝对路径；失败时为 null。</param>
/// <param name="ErrorCode">失败错误码：PATH_INVALID / PATH_ESCAPE / SCOPE_DENIED / SENSITIVE_PATH。</param>
/// <param name="ErrorMessage">可诊断的中文说明。</param>
public sealed record PathCheckResult(bool Success, string? FullPath, string? ErrorCode, string? ErrorMessage)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="fullPath">规范化后的绝对路径。</param>
    public static PathCheckResult Ok(string fullPath) => new(true, fullPath, null, null);

    /// <summary>构造失败结果。</summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">说明。</param>
    public static PathCheckResult Fail(string code, string message) => new(false, null, code, message);
}

/// <summary>
/// 路径安全门：把模型给出的相对路径解析为"可读/可写"的绝对路径。
/// 规则（设计文档 §10.3）：必须落在工作区内；写操作还必须落在 <c>writeScope</c> 内；
/// <c>..</c>、跨盘符、符号链接/junction 逃逸与敏感路径一律拒绝。
/// </summary>
public static class PathGuard
{
    private static readonly string[] _sensitiveExtensions = { ".pem", ".key", ".pfx", ".p12" };
    private static readonly string[] _sensitiveDirectories = { ".git", ".ssh", "secrets" };

    /// <summary>
    /// 校验只读路径：必须在工作区内，且不是敏感路径。
    /// </summary>
    /// <param name="workspaceRoot">工作区根目录绝对路径。</param>
    /// <param name="relativePath">模型给出的相对路径。</param>
    public static PathCheckResult ResolveRead(string workspaceRoot, string relativePath) =>
        ResolveInside(workspaceRoot, relativePath, writeScope: null, enforceScope: false);

    /// <summary>
    /// 校验写路径：必须在写根内；<paramref name="enforceScope"/> 为 true 时还必须落在 <paramref name="writeScope"/> 前缀内。
    /// </summary>
    /// <param name="writeRoot">写根：auto 档位为工作区根，dispatch_only 档位为隔离目录。</param>
    /// <param name="relativePath">模型给出的相对路径。</param>
    /// <param name="writeScope">任务声明的写作用域（相对路径前缀）；null 表示不做作用域限制。</param>
    /// <param name="enforceScope">是否强制作用域校验。</param>
    public static PathCheckResult ResolveWrite(
        string writeRoot,
        string relativePath,
        IReadOnlyList<string>? writeScope,
        bool enforceScope) =>
        ResolveInside(writeRoot, relativePath, writeScope, enforceScope);

    /// <summary>判断相对路径是否命中敏感路径规则（.git、.env*、secrets、*.pem、*.key 等）。</summary>
    /// <param name="relativePath">相对路径（任意分隔符）。</param>
    public static bool IsSensitive(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (_sensitiveDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            if (segment.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var extension = Path.GetExtension(normalized);
        return !string.IsNullOrEmpty(extension) &&
               _sensitiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>把相对路径规范化为以 <c>/</c> 分隔的形式，用于 artifacts 输出与作用域比较。</summary>
    /// <param name="relativePath">相对路径。</param>
    public static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// 解析并校验路径。拒绝绝对路径、<c>..</c>、盘符切换与符号链接逃逸。
    /// </summary>
    /// <param name="root">根目录绝对路径。</param>
    /// <param name="relativePath">相对路径。</param>
    /// <param name="writeScope">写作用域；仅写校验使用。</param>
    /// <param name="enforceScope">是否强制作用域。</param>
    private static PathCheckResult ResolveInside(
        string root,
        string relativePath,
        IReadOnlyList<string>? writeScope,
        bool enforceScope)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return PathCheckResult.Fail("PATH_INVALID", "路径不能为空。");
        }

        if (Path.IsPathRooted(relativePath))
        {
            return PathCheckResult.Fail("PATH_INVALID", $"只允许相对路径，收到绝对路径：{relativePath}");
        }

        var normalized = Normalize(relativePath);
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == ".."))
        {
            return PathCheckResult.Fail("PATH_ESCAPE", $"路径不得包含 ..：{relativePath}");
        }

        if (IsSensitive(normalized))
        {
            return PathCheckResult.Fail("SENSITIVE_PATH", $"拒绝访问敏感路径：{relativePath}");
        }

        var rootFull = EnsureTrailingSeparator(Path.GetFullPath(root));
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(rootFull, normalized));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathCheckResult.Fail("PATH_INVALID", $"路径非法：{exception.Message}");
        }

        // 根目录自身（如 "."、"./"）规范化后不带尾分隔符，直接 StartsWith 会把合法的根路径误判为越界；
        // 判定包含关系前统一补齐尾分隔符，路径本身仍返回未补分隔符的形式。
        if (!EnsureTrailingSeparator(fullPath).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return PathCheckResult.Fail("PATH_ESCAPE", $"路径越界，必须位于 {root} 内：{relativePath}");
        }

        if (enforceScope)
        {
            if (!IsWithinScope(fullPath, rootFull, writeScope))
            {
                var scopes = writeScope is { Count: > 0 } ? string.Join(", ", writeScope) : "（空）";
                return PathCheckResult.Fail(
                    "SCOPE_DENIED",
                    $"写入超出任务 writeScope（{scopes}）：{relativePath}");
            }
        }

        if (HasReparsePoint(rootFull, normalized))
        {
            return PathCheckResult.Fail("PATH_ESCAPE", $"路径经过符号链接或 junction，已拒绝：{relativePath}");
        }

        return PathCheckResult.Ok(TrimTrailingSeparator(fullPath));
    }

    /// <summary>判断绝对路径是否落在某个作用域前缀内。</summary>
    /// <param name="fullPath">绝对路径。</param>
    /// <param name="rootFull">带尾分隔符的根路径。</param>
    /// <param name="writeScope">作用域前缀集合。</param>
    private static bool IsWithinScope(string fullPath, string rootFull, IReadOnlyList<string>? writeScope)
    {
        if (writeScope is null || writeScope.Count == 0)
        {
            return false;
        }

        // 作用域前缀同样按带尾分隔符比较：writeScope 为 "." 或根目录自身时不误判。
        var fullPathWithSeparator = EnsureTrailingSeparator(fullPath);
        foreach (var scope in writeScope)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                continue;
            }

            var scopeFull = EnsureTrailingSeparator(
                Path.GetFullPath(Path.Combine(rootFull, Normalize(scope))));

            if (fullPathWithSeparator.StartsWith(scopeFull, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>从根开始逐段检查已存在的路径节点，阻止通过符号链接/junction 逃逸。</summary>
    /// <param name="rootFull">带尾分隔符的根路径。</param>
    /// <param name="relativePath">规范化后的相对路径。</param>
    private static bool HasReparsePoint(string rootFull, string relativePath)
    {
        var current = rootFull;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            // "." 指向当前目录（根目录自身），不是可逃逸节点，跳过以免把根误判为符号链接。
            if (segment == ".")
            {
                continue;
            }

            current = Path.Combine(current, segment);

            if (Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                continue;
            }

            if (File.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }

            // 其余段尚不存在：后面的段一定是新建的普通文件/目录，无需继续检查。
            break;
        }

        return false;
    }

    /// <summary>确保路径以目录分隔符结尾，避免 <c>C:\work</c> 误匹配 <c>C:\workshop</c>。</summary>
    /// <param name="path">绝对路径。</param>
    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    /// <summary>
    /// 去掉非根路径的尾分隔符，保证同一目录不同写法（<c>.</c>、<c>./</c>、<c>src/</c>）返回一致结果；
    /// 盘符根或共享根（<c>C:\</c>、<c>/</c>）保留原样，避免退化成 <c>C:</c>。
    /// </summary>
    /// <param name="path">绝对路径。</param>
    private static string TrimTrailingSeparator(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return trimmed.Length <= root.Length ? path : trimmed;
    }
}
