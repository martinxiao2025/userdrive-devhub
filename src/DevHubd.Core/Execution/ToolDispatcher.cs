using System.Text;
using System.Text.Json;
using DevHubd.Core.Input;
using DevHubd.Core.Models;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Execution;

/// <summary>
/// 一个回合的读写上下文：读写必须同根，保证模型能读到自己在上一回合写入的内容。
/// </summary>
/// <param name="Root">读写根：auto 档位为工作区根，dispatch_only 档位为隔离工作目录。</param>
/// <param name="WriteScope">任务声明的写作用域；null 表示不限制（隔离目录场景）。</param>
/// <param name="EnforceScope">是否强制 writeScope 校验。</param>
public sealed record TurnWorkspace(
    string Root,
    IReadOnlyList<string>? WriteScope,
    bool EnforceScope);

/// <summary>
/// 工具执行器：只暴露设计文档 §10.4 的白名单工具，越界、敏感路径与超预算一律以失败结果返回。
/// <c>run_command</c> 默认不提供（自主执行 + 任意命令 = 不可控）。
/// </summary>
public sealed class ToolDispatcher : IToolDispatcher
{
    private const int MaxListEntries = 200;
    private const int MaxSearchFiles = 500;
    private const int MaxSearchResults = 100;
    private const int MaxQuestionLength = 500;
    private const int MaxContextLength = 4000;
    private const int MaxOptions = 8;

    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly ToolDescriptor[] _descriptors =
    {
        new(
            "read_file",
            "读取工作区内的文本文件。路径必须相对工作区根。",
            """{"type":"object","properties":{"path":{"type":"string","description":"相对工作区根的路径"}},"required":["path"],"additionalProperties":false}"""),
        new(
            "list_dir",
            "列出工作区内的目录项，深度不超过 4。",
            """{"type":"object","properties":{"path":{"type":"string","description":"相对工作区根的目录，默认 ."},"depth":{"type":"integer","minimum":1,"maximum":4}},"additionalProperties":false}"""),
        new(
            "search",
            "在工作区内做文本检索，最多返回 100 条命中。",
            """{"type":"object","properties":{"query":{"type":"string"},"path":{"type":"string","description":"相对工作区根，默认 ."},"limit":{"type":"integer","minimum":1,"maximum":100}},"required":["query"],"additionalProperties":false}"""),
        new(
            "write_file",
            "写入文本文件。仅允许写作用域内；敏感路径与超预算写入会被拒绝。",
            """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}"""),
        new(
            "apply_patch",
            "对文件做一次精确替换：find 必须唯一命中，否则拒绝。",
            """{"type":"object","properties":{"path":{"type":"string"},"find":{"type":"string"},"replace":{"type":"string"}},"required":["path","find","replace"],"additionalProperties":false}"""),
        new(
            "ask_input",
            "缺少必要信息时向用户提问：问题单会落库并挂起任务，等人回答后继续。每回合最多提问一次。",
            """{"type":"object","properties":{"question":{"type":"string","description":"一句话问题"},"context":{"type":"string","description":"摘要、相对路径与已脱敏片段；禁止写入凭据"},"options":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"label":{"type":"string"},"value":{"type":"string"},"recommended":{"type":"boolean"}},"required":["id","label"],"additionalProperties":false}},"missingInputs":{"type":"array","items":{"type":"string"}}},"required":["question"],"additionalProperties":false}"""),
    };

    private const string AskInputToolName = "ask_input";

    private readonly TurnWorkspace _workspace;
    private readonly BudgetLimits _budget;
    private readonly ILogger<ToolDispatcher> _logger;
    private readonly bool _canRequestInput;
    private readonly Func<InputPrompt, CancellationToken, Task<InputAcceptance>>? _askInput;
    private readonly List<string> _changedFiles = new();
    private int _writes;
    private string? _inputRequestId;
    private string? _inputQuestion;

    /// <summary>注入回合上下文、预算与日志器；提问能力由角色声明决定。</summary>
    /// <param name="workspace">回合写入上下文。</param>
    /// <param name="budget">预算上限。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="canRequestInput">该角色是否允许提问（<c>canRequestInput</c>）。</param>
    /// <param name="askInput">提问落库回调；为 null 表示不可提问。</param>
    public ToolDispatcher(
        TurnWorkspace workspace,
        BudgetLimits budget,
        ILogger<ToolDispatcher> logger,
        bool canRequestInput = false,
        Func<InputPrompt, CancellationToken, Task<InputAcceptance>>? askInput = null)
    {
        _workspace = workspace;
        _budget = budget;
        _logger = logger;
        _canRequestInput = canRequestInput && askInput is not null;
        _askInput = askInput;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ChangedFiles => _changedFiles;

    /// <inheritdoc />
    public int ExecutedCalls { get; private set; }

    /// <summary>本回合写入的问题单 id；null 表示未提问。</summary>
    public string? InputRequestId => _inputRequestId;

    /// <summary>本回合提出的问题文本；未提问为 null。</summary>
    public string? InputQuestion => _inputQuestion;

    /// <inheritdoc />
    public IReadOnlyList<ToolDescriptor> Describe() => _canRequestInput
        ? _descriptors
        : _descriptors.Where(descriptor => !string.Equals(descriptor.Name, AskInputToolName, StringComparison.Ordinal)).ToArray();

    /// <summary>工具白名单名称（只读资源 <c>devhub://limits</c> 展示用）。</summary>
    public static IReadOnlyList<string> AllowedToolNames => _allowedToolNames;

    private static readonly string[] _allowedToolNames = _descriptors.Select(descriptor => descriptor.Name).ToArray();

    /// <inheritdoc />
    public async Task<ToolExecutionResult> ExecuteAsync(
        string turnId,
        string toolName,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        ExecutedCalls++;

        // 诊断日志只记录工具名与结果码，不记录文件内容，避免泄漏工作区数据。
        _logger.LogDebug("工具调用：turnId={TurnId} tool={ToolName}", turnId, toolName);

        return toolName switch
        {
            "read_file" => await ReadFileAsync(input, cancellationToken).ConfigureAwait(false),
            "list_dir" => ListDirectory(input),
            "search" => await SearchAsync(input, cancellationToken).ConfigureAwait(false),
            "write_file" => await WriteFileAsync(input, cancellationToken).ConfigureAwait(false),
            "apply_patch" => await ApplyPatchAsync(input, cancellationToken).ConfigureAwait(false),
            AskInputToolName => await AskInputAsync(input, cancellationToken).ConfigureAwait(false),
            _ => ToolExecutionResult.Fail(DevHubErrorCodes.UnknownTool, $"工具不在白名单内：{toolName}"),
        };
    }

    /// <summary>
    /// 提问：校验参数、限制每回合一次，并把问题单落到持久化层。
    /// 落库成功后返回 requestId；回合结束由 <c>TurnRunner</c> 转成 <c>needs_input</c> 收尾。
    /// </summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> AskInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!_canRequestInput || _askInput is null)
        {
            return ToolExecutionResult.Fail(
                DevHubErrorCodes.InputNotAllowed,
                "当前角色不允许向用户提问。");
        }

        if (_inputRequestId is not null)
        {
            return ToolExecutionResult.Ok(
                $"已有待回答请求（{_inputRequestId}），请不要重复提问，直接结束本回合。");
        }

        if (!TryGetString(input, "question", out var question) || string.IsNullOrWhiteSpace(question))
        {
            return ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, "缺少 question 参数。");
        }

        question = question.Trim();
        if (question.Length > MaxQuestionLength)
        {
            return ToolExecutionResult.Fail(
                DevHubErrorCodes.InvalidArgument,
                $"question 超过 {MaxQuestionLength} 字符上限。");
        }

        var context = TryGetString(input, "context", out var rawContext) ? rawContext.Trim() : null;
        if (context is { Length: 0 })
        {
            context = null;
        }

        if (context is not null && context.Length > MaxContextLength)
        {
            return ToolExecutionResult.Fail(
                DevHubErrorCodes.InvalidArgument,
                $"context 超过 {MaxContextLength} 字符上限。");
        }

        var options = ParseOptions(input, out var optionsError);
        if (optionsError is not null)
        {
            return ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, optionsError);
        }

        var missingInputs = ParseStringArray(input, "missingInputs");
        if (missingInputs.Count > MaxOptions)
        {
            return ToolExecutionResult.Fail(
                DevHubErrorCodes.InvalidArgument,
                $"missingInputs 最多 {MaxOptions} 项。");
        }

        var acceptance = await _askInput(
            new InputPrompt(question, context, options, missingInputs),
            cancellationToken).ConfigureAwait(false);

        if (!acceptance.Accepted || acceptance.RequestId is null)
        {
            return ToolExecutionResult.Fail(
                acceptance.ErrorCode ?? DevHubErrorCodes.Internal,
                acceptance.ErrorMessage ?? "问题单写入失败。");
        }

        _inputRequestId = acceptance.RequestId;
        _inputQuestion = question;
        return ToolExecutionResult.Ok(
            $"问题已记录（{acceptance.RequestId}）。本回合到此结束，请用一句话说明因缺少输入而暂停。");
    }

    /// <summary>解析 <c>options</c> 数组；缺失视为空数组，格式非法返回错误说明。</summary>
    /// <param name="input">入参。</param>
    /// <param name="error">错误说明；null 表示解析成功。</param>
    private static IReadOnlyList<InputOption> ParseOptions(JsonElement input, out string? error)
    {
        error = null;
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("options", out var element) ||
            element.ValueKind == JsonValueKind.Null ||
            element.ValueKind == JsonValueKind.Undefined)
        {
            return Array.Empty<InputOption>();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            error = "options 必须是数组。";
            return Array.Empty<InputOption>();
        }

        var options = new List<InputOption>();
        foreach (var option in element.EnumerateArray())
        {
            if (options.Count >= MaxOptions)
            {
                error = $"options 最多 {MaxOptions} 项。";
                return Array.Empty<InputOption>();
            }

            if (!TryGetString(option, "id", out var id) || string.IsNullOrWhiteSpace(id) ||
                !TryGetString(option, "label", out var label) || string.IsNullOrWhiteSpace(label))
            {
                error = "options 每项必须包含非空 id 与 label。";
                return Array.Empty<InputOption>();
            }

            var value = TryGetString(option, "value", out var rawValue) ? rawValue : null;
            var recommended = option.TryGetProperty("recommended", out var flag) &&
                              flag.ValueKind == JsonValueKind.True;

            if (options.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal)))
            {
                error = $"options 存在重复 id：{id}。";
                return Array.Empty<InputOption>();
            }

            options.Add(new InputOption(id, label, value, recommended));
        }

        return options;
    }

    /// <summary>解析字符串数组参数；缺失或非数组时返回空数组。</summary>
    /// <param name="input">入参。</param>
    /// <param name="name">字段名。</param>
    private static IReadOnlyList<string> ParseStringArray(JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return element
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToArray();
    }

    /// <summary>读文件：工作区内、不超过预算的读取上限。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> ReadFileAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryGetString(input, "path", out var relativePath))
        {
            return ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, "缺少 path 参数。");
        }

        var check = PathGuard.ResolveRead(_workspace.Root, relativePath);
        if (!check.Success)
        {
            return ToolExecutionResult.Fail(check.ErrorCode!, check.ErrorMessage!);
        }

        try
        {
            var info = new FileInfo(check.FullPath!);
            if (!info.Exists)
            {
                return ToolExecutionResult.Fail("FILE_NOT_FOUND", $"文件不存在：{relativePath}");
            }

            if (info.Length > _budget.MaxReadBytes)
            {
                return ToolExecutionResult.Fail(
                    "FILE_TOO_LARGE",
                    $"文件超过读取上限 {_budget.MaxReadBytes} 字节：{relativePath}");
            }

            var bytes = await File.ReadAllBytesAsync(check.FullPath!, cancellationToken).ConfigureAwait(false);
            return ToolExecutionResult.Ok(_utf8NoBom.GetString(bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolExecutionResult.Fail("IO_ERROR", $"读取失败：{exception.Message}");
        }
    }

    /// <summary>列目录：工作区内、深度不超过 4。</summary>
    /// <param name="input">入参。</param>
    private ToolExecutionResult ListDirectory(JsonElement input)
    {
        var relativePath = TryGetString(input, "path", out var value) ? value : ".";
        var depth = TryGetInt32(input, "depth", out var requested) ? Math.Clamp(requested, 1, 4) : 1;

        var check = PathGuard.ResolveRead(_workspace.Root, relativePath);
        if (!check.Success)
        {
            return ToolExecutionResult.Fail(check.ErrorCode!, check.ErrorMessage!);
        }

        if (!Directory.Exists(check.FullPath))
        {
            return ToolExecutionResult.Fail("DIR_NOT_FOUND", $"目录不存在：{relativePath}");
        }

        var lines = new List<string>();
        try
        {
            Walk(new DirectoryInfo(check.FullPath!), depth, lines);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolExecutionResult.Fail("IO_ERROR", $"列目录失败：{exception.Message}");
        }

        return ToolExecutionResult.Ok(string.Join('\n', lines));
    }

    /// <summary>递归收集目录项，受最大条数限制。</summary>
    /// <param name="directory">当前目录。</param>
    /// <param name="remainingDepth">剩余深度。</param>
    /// <param name="lines">结果缓冲。</param>
    private static void Walk(DirectoryInfo directory, int remainingDepth, List<string> lines)
    {
        if (remainingDepth <= 0)
        {
            return;
        }

        foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (lines.Count >= MaxListEntries)
            {
                lines.Add("…（已达条数上限）");
                return;
            }

            if (PathGuard.IsSensitive(entry.Name))
            {
                continue;
            }

            lines.Add(entry is DirectoryInfo ? $"{entry.Name}/" : entry.Name);

            if (entry is DirectoryInfo child && remainingDepth > 1)
            {
                Walk(child, remainingDepth - 1, lines);
            }
        }
    }

    /// <summary>文本检索：工作区内、最多 100 条命中。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> SearchAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryGetString(input, "query", out var query) || query.Length == 0)
        {
            return ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, "缺少 query 参数。");
        }

        var relativePath = TryGetString(input, "path", out var path) ? path : ".";
        var limit = TryGetInt32(input, "limit", out var requested) ? Math.Clamp(requested, 1, MaxSearchResults) : MaxSearchResults;

        var check = PathGuard.ResolveRead(_workspace.Root, relativePath);
        if (!check.Success)
        {
            return ToolExecutionResult.Fail(check.ErrorCode!, check.ErrorMessage!);
        }

        if (!Directory.Exists(check.FullPath))
        {
            return ToolExecutionResult.Fail("DIR_NOT_FOUND", $"目录不存在：{relativePath}");
        }

        var results = new List<string>();
        var scanned = 0;

        // 用 EnumerationOptions 而不是 SearchOption.AllDirectories：
        // 后者遇到无权限子目录会抛 UnauthorizedAccessException，一个不可读目录就能让整个回合失败。
        // AttributesToSkip=0 保持原行为（不跳过隐藏/系统文件）。
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        foreach (var file in Directory.EnumerateFiles(check.FullPath!, "*", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++scanned > MaxSearchFiles || results.Count >= limit)
            {
                break;
            }

            var relative = Path.GetRelativePath(_workspace.Root, file);
            if (PathGuard.IsSensitive(relative))
            {
                continue;
            }

            string text;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > _budget.MaxReadBytes)
                {
                    continue;
                }

                text = await File.ReadAllTextAsync(file, _utf8NoBom, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in text.Split('\n'))
            {
                lineNumber++;
                if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add($"{PathGuard.Normalize(relative)}:{lineNumber}: {line.Trim()}");
                    if (results.Count >= limit)
                    {
                        break;
                    }
                }
            }
        }

        return ToolExecutionResult.Ok(results.Count == 0 ? "（无命中）" : string.Join('\n', results));
    }

    /// <summary>写文件：写根内 + 作用域 + 预算。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> WriteFileAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryGetString(input, "path", out var relativePath) ||
            !TryGetString(input, "content", out var content))
        {
            return ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, "缺少 path 或 content 参数。");
        }

        var bytes = _utf8NoBom.GetBytes(content);
        var guarded = GuardWrite(relativePath, bytes.LongLength);
        if (guarded is not null)
        {
            return guarded;
        }

        var check = PathGuard.ResolveWrite(
            _workspace.Root,
            relativePath,
            _workspace.WriteScope,
            _workspace.EnforceScope);
        if (!check.Success)
        {
            return ToolExecutionResult.Fail(check.ErrorCode!, check.ErrorMessage!);
        }

        try
        {
            var directory = Path.GetDirectoryName(check.FullPath!);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(check.FullPath!, bytes, cancellationToken).ConfigureAwait(false);
            RecordWrite(relativePath);
            return ToolExecutionResult.Ok($"已写入 {PathGuard.Normalize(relativePath)}（{bytes.Length} 字节）");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolExecutionResult.Fail("IO_ERROR", $"写入失败：{exception.Message}");
        }
    }

    /// <summary>精确替换：find 必须唯一命中。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> ApplyPatchAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryGetString(input, "path", out var relativePath) ||
            !TryGetString(input, "find", out var find) ||
            !TryGetString(input, "replace", out var replace))
        {
            return ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, "缺少 path、find 或 replace 参数。");
        }

        if (find.Length == 0)
        {
            return ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, "find 不能为空。");
        }

        var check = PathGuard.ResolveWrite(
            _workspace.Root,
            relativePath,
            _workspace.WriteScope,
            _workspace.EnforceScope);
        if (!check.Success)
        {
            return ToolExecutionResult.Fail(check.ErrorCode!, check.ErrorMessage!);
        }

        try
        {
            if (!File.Exists(check.FullPath))
            {
                return ToolExecutionResult.Fail("FILE_NOT_FOUND", $"文件不存在：{relativePath}");
            }

            var original = await File.ReadAllTextAsync(check.FullPath!, _utf8NoBom, cancellationToken)
                .ConfigureAwait(false);

            var first = original.IndexOf(find, StringComparison.Ordinal);
            if (first < 0)
            {
                return ToolExecutionResult.Fail("PATCH_NOT_MATCHED", "find 未命中，拒绝猜测。");
            }

            if (original.IndexOf(find, first + find.Length, StringComparison.Ordinal) >= 0)
            {
                return ToolExecutionResult.Fail("PATCH_NOT_UNIQUE", "find 命中多处，拒绝替换。");
            }

            var updated = string.Concat(original.AsSpan(0, first), replace, original.AsSpan(first + find.Length));
            var bytes = _utf8NoBom.GetBytes(updated);

            var guarded = GuardWrite(relativePath, bytes.LongLength);
            if (guarded is not null)
            {
                return guarded;
            }

            await File.WriteAllBytesAsync(check.FullPath, bytes, cancellationToken).ConfigureAwait(false);
            RecordWrite(relativePath);
            return ToolExecutionResult.Ok($"已替换 {PathGuard.Normalize(relativePath)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ToolExecutionResult.Fail("IO_ERROR", $"替换失败：{exception.Message}");
        }
    }

    /// <summary>写入前的预算检查：单次字节数、写入次数、改动文件数。</summary>
    /// <param name="relativePath">目标相对路径。</param>
    /// <param name="byteCount">本次写入字节数。</param>
    private ToolExecutionResult? GuardWrite(string relativePath, long byteCount)
    {
        if (byteCount > _budget.MaxPatchBytes)
        {
            return ToolExecutionResult.Fail(
                DevHubErrorCodes.BudgetExceeded,
                $"单次写入 {byteCount} 字节超过上限 {_budget.MaxPatchBytes}。");
        }

        if (_writes >= _budget.MaxWrites)
        {
            return ToolExecutionResult.Fail(
                DevHubErrorCodes.BudgetExceeded,
                $"写入次数已达上限 {_budget.MaxWrites}。");
        }

        var normalized = PathGuard.Normalize(relativePath);
        if (!_changedFiles.Contains(normalized, StringComparer.OrdinalIgnoreCase) &&
            _changedFiles.Count >= _budget.MaxFilesChanged)
        {
            return ToolExecutionResult.Fail(
                DevHubErrorCodes.BudgetExceeded,
                $"改动文件数已达上限 {_budget.MaxFilesChanged}。");
        }

        return null;
    }

    /// <summary>记录一次成功写入。</summary>
    /// <param name="relativePath">目标相对路径。</param>
    private void RecordWrite(string relativePath)
    {
        _writes++;
        var normalized = PathGuard.Normalize(relativePath);
        if (!_changedFiles.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _changedFiles.Add(normalized);
        }
    }

    /// <summary>读取字符串参数。</summary>
    /// <param name="input">入参对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="value">字段值。</param>
    private static bool TryGetString(JsonElement input, string name, out string value)
    {
        value = string.Empty;
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>读取整数参数。</summary>
    /// <param name="input">入参对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="value">字段值。</param>
    private static bool TryGetInt32(JsonElement input, string name, out int value)
    {
        value = 0;
        return input.ValueKind == JsonValueKind.Object &&
               input.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value);
    }
}
