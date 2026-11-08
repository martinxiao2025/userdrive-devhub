using System.Text.Json;
using DevHubd.Core.Execution;
using DevHubd.Core.Models;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using DevHubd.Mcp.Tools;
using Microsoft.Extensions.Logging;

namespace DevHubd.Mcp.Turns;

/// <summary>
/// 后台回合的服务层工具实现：把 <c>task_upsert</c>、<c>queue_*</c>、<c>result_*</c>、
/// <c>scheduler_status</c> 与 <c>input_request_*</c> 接到与聊天侧相同的服务层接口，
/// 保证"后台回合能做什么"由角色声明决定，而不是由会话侧代录。
/// </summary>
public sealed class McpTurnToolService : ITurnToolService
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    private static readonly ToolDescriptor[] _descriptors =
    {
        new(
            "task_upsert",
            "创建或覆盖任务文件（.devhub/tasks/<id>.json），由扫描器在下一轮拾取。相同内容会被去重。",
            """{"type":"object","properties":{"id":{"type":"string"},"title":{"type":"string"},"prompt":{"type":"string"},"workspaceId":{"type":"string"},"priority":{"type":"integer","minimum":0,"maximum":1000},"role":{"type":"string"},"delivery":{"type":"string"},"writeScope":{"type":"array","items":{"type":"string"}},"dependsOn":{"type":"array","items":{"type":"string"}},"requiresApproval":{"type":"boolean"},"source":{"type":"string"}},"required":["id","title","prompt"],"additionalProperties":false}"""),
        new(
            "queue_list",
            "分页列出队列项。",
            """{"type":"object","properties":{"status":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":100},"cursor":{"type":"string"}},"additionalProperties":false}"""),
        new(
            "queue_get",
            "读取单个队列项详情与尝试记录。",
            """{"type":"object","properties":{"queueItemId":{"type":"string"}},"required":["queueItemId"],"additionalProperties":false}"""),
        new(
            "scheduler_status",
            "读取调度器状态快照（档位、队列计数、provider 与角色）。",
            """{"type":"object","properties":{},"additionalProperties":false}"""),
        new(
            "result_list",
            "分页列出执行结果。",
            """{"type":"object","properties":{"unreadOnly":{"type":"boolean"},"limit":{"type":"integer","minimum":1,"maximum":100},"cursor":{"type":"string"}},"additionalProperties":false}"""),
        new(
            "result_ack",
            "把指定结果（或全部未读）标记为已读。",
            """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"}},"all":{"type":"boolean"}},"additionalProperties":false}"""),
        new(
            "result_approve",
            "审批通过一条待审批结果：结果置为 delivered，任务置为 done。",
            """{"type":"object","properties":{"resultId":{"type":"string"},"comment":{"type":"string"}},"required":["resultId"],"additionalProperties":false}"""),
        new(
            "result_reject",
            "驳回一条待审批结果：结果置为 failed，任务回到 ready 等待重做。",
            """{"type":"object","properties":{"resultId":{"type":"string"},"comment":{"type":"string"}},"required":["resultId"],"additionalProperties":false}"""),
        new(
            "input_request_list",
            "分页列出问题单。",
            """{"type":"object","properties":{"workspaceId":{"type":"string"},"taskId":{"type":"string"},"status":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":100},"cursor":{"type":"string"}},"additionalProperties":false}"""),
        new(
            "input_request_get",
            "读取单个问题单详情。",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}"""),
    };

    private readonly ITaskWriteService _tasks;
    private readonly IQueueQuerySource _queues;
    private readonly IResultQuerySource _results;
    private readonly IResultDecisionSource _decisions;
    private readonly IInputRequestService _inputs;
    private readonly ISchedulerStatusSource _scheduler;
    private readonly ILogger<McpTurnToolService> _logger;

    /// <summary>注入服务层数据源与日志器。</summary>
    /// <param name="tasks">任务写入服务。</param>
    /// <param name="queues">队列查询。</param>
    /// <param name="results">结果查询。</param>
    /// <param name="decisions">结果审批闸门。</param>
    /// <param name="inputs">问题单服务。</param>
    /// <param name="scheduler">调度状态源。</param>
    /// <param name="logger">日志器。</param>
    public McpTurnToolService(
        ITaskWriteService tasks,
        IQueueQuerySource queues,
        IResultQuerySource results,
        IResultDecisionSource decisions,
        IInputRequestService inputs,
        ISchedulerStatusSource scheduler,
        ILogger<McpTurnToolService> logger)
    {
        _tasks = tasks;
        _queues = queues;
        _results = results;
        _decisions = decisions;
        _inputs = inputs;
        _scheduler = scheduler;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDescriptor> Describe() => _descriptors;

    /// <inheritdoc />
    public async Task<ToolExecutionResult> InvokeAsync(
        string toolName,
        JsonElement input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return toolName switch
            {
                "task_upsert" => await UpsertTaskAsync(input, cancellationToken).ConfigureAwait(false),
                "queue_list" => await ListQueuesAsync(input, cancellationToken).ConfigureAwait(false),
                "queue_get" => await GetQueueAsync(input, cancellationToken).ConfigureAwait(false),
                "scheduler_status" => await SchedulerStatusAsync(cancellationToken).ConfigureAwait(false),
                "result_list" => await ListResultsAsync(input, cancellationToken).ConfigureAwait(false),
                "result_ack" => await AckResultsAsync(input, cancellationToken).ConfigureAwait(false),
                "result_approve" => await DecideAsync(input, approve: true, cancellationToken).ConfigureAwait(false),
                "result_reject" => await DecideAsync(input, approve: false, cancellationToken).ConfigureAwait(false),
                "input_request_list" => await ListInputsAsync(input, cancellationToken).ConfigureAwait(false),
                "input_request_get" => await GetInputAsync(input, cancellationToken).ConfigureAwait(false),
                _ => ToolExecutionResult.Fail(DevHubErrorCodes.UnknownTool, $"未知服务层工具：{toolName}"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 服务层异常必须留痕，但不把内部堆栈暴露给模型。
            _logger.LogError(exception, "服务层工具执行失败：tool={ToolName}", toolName);
            return ToolExecutionResult.Fail(
                DevHubErrorCodes.Internal,
                $"服务层工具执行失败：{exception.Message}");
        }
    }

    /// <summary>写入或覆盖任务文件。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> UpsertTaskAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryGetRequiredString(input, "id", out var id) ||
            !TryGetRequiredString(input, "title", out var title) ||
            !TryGetRequiredString(input, "prompt", out var prompt))
        {
            return ToolExecutionResult.Fail(
                DevHubErrorCodes.InvalidArgument,
                "task_upsert 缺少必填参数：id / title / prompt。");
        }

        var request = new TaskUpsertRequest(
            GetString(input, "workspaceId"),
            id,
            title,
            prompt,
            GetInt(input, "priority", 0),
            GetString(input, "role"),
            GetString(input, "delivery"),
            GetStringArray(input, "writeScope"),
            GetStringArray(input, "dependsOn"),
            GetBool(input, "requiresApproval", false),
            GetString(input, "source"));

        var outcome = await _tasks.UpsertAsync(request, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            TaskUpsertStatus.Written => ToolExecutionResult.Ok(JsonSerializer.Serialize(
                new TaskUpsertResult(outcome.TaskId, outcome.Path!, outcome.ContentHash!, outcome.Queued),
                ToolJson.Options)),
            TaskUpsertStatus.UnknownWorkspace => ToolExecutionResult.Fail(
                "UNKNOWN_WORKSPACE",
                "工作区不存在或未启用。"),
            TaskUpsertStatus.AmbiguousWorkspace => ToolExecutionResult.Fail(
                "AMBIGUOUS_WORKSPACE",
                $"存在多个已启用工作区，请显式指定 workspaceId：{string.Join(", ", outcome.Candidates ?? Array.Empty<string>())}"),
            TaskUpsertStatus.UnknownRole => ToolExecutionResult.Fail(
                "INVALID_ROLE",
                outcome.Reason ?? "角色未在 role_view 中声明。"),
            TaskUpsertStatus.InvalidTask => ToolExecutionResult.Fail(
                DevHubErrorCodes.InvalidArgument,
                outcome.Reason ?? "任务字段不合法。"),
            TaskUpsertStatus.Conflict => ToolExecutionResult.Fail(
                "TASK_CONFLICT",
                outcome.Reason ?? "任务正在执行，拒绝改写。"),
            _ => ToolExecutionResult.Fail(DevHubErrorCodes.Internal, $"未知的写入状态：{outcome.Status}"),
        };
    }

    /// <summary>分页列出队列项。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> ListQueuesAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var status = GetString(input, "status") ?? "all";
        if (!TryGetLimit(input, out var limit))
        {
            return InvalidLimit();
        }

        var page = await _queues.ListAsync(status, limit, GetString(input, "cursor"), cancellationToken)
            .ConfigureAwait(false);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(page, ToolJson.Options));
    }

    /// <summary>读取队列项详情。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> GetQueueAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryGetRequiredString(input, "queueItemId", out var queueItemId))
        {
            return ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, "缺少 queueItemId。");
        }

        var detail = await _queues.GetAsync(queueItemId, cancellationToken).ConfigureAwait(false);
        return detail is null
            ? ToolExecutionResult.Fail("NOT_FOUND", $"队列项不存在：{queueItemId}")
            : ToolExecutionResult.Ok(JsonSerializer.Serialize(detail, ToolJson.Options));
    }

    /// <summary>读取调度状态快照。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> SchedulerStatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _scheduler.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(snapshot, ToolJson.Options));
    }

    /// <summary>分页列出结果。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> ListResultsAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryGetLimit(input, out var limit))
        {
            return InvalidLimit();
        }

        var page = await _results
            .ListAsync(GetBool(input, "unreadOnly", false), limit, GetString(input, "cursor"), cancellationToken)
            .ConfigureAwait(false);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(page, ToolJson.Options));
    }

    /// <summary>确认结果已读。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> AckResultsAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var all = GetBool(input, "all", false);
        var ids = GetStringArray(input, "ids");

        if (!all && (ids is null || ids.Count == 0))
        {
            return ToolExecutionResult.Fail(
                DevHubErrorCodes.InvalidArgument,
                "必须提供 ids，或显式设置 all=true。");
        }

        var result = all
            ? await _results.AckAllAsync(cancellationToken).ConfigureAwait(false)
            : await _results.AckAsync(ids!, cancellationToken).ConfigureAwait(false);

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(result, ToolJson.Options));
    }

    /// <summary>审批通过或驳回结果。</summary>
    /// <param name="input">入参。</param>
    /// <param name="approve">true 通过，false 驳回。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> DecideAsync(
        JsonElement input,
        bool approve,
        CancellationToken cancellationToken)
    {
        if (!TryGetRequiredString(input, "resultId", out var resultId))
        {
            return ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, "缺少 resultId。");
        }

        var (status, result, reason) = await _decisions
            .DecideAsync(resultId, approve, GetString(input, "comment"), "turn", cancellationToken)
            .ConfigureAwait(false);

        return status switch
        {
            ResultDecisionStatus.Decided or ResultDecisionStatus.Duplicate =>
                ToolExecutionResult.Ok(JsonSerializer.Serialize(result, ToolJson.Options)),
            ResultDecisionStatus.NotFound => ToolExecutionResult.Fail(
                "NOT_FOUND",
                $"结果不存在：{resultId}"),
            ResultDecisionStatus.NotPending => ToolExecutionResult.Fail(
                "NOT_PENDING",
                reason ?? "结果不在待审批状态。"),
            ResultDecisionStatus.FileWriteFailed => ToolExecutionResult.Fail(
                DevHubErrorCodes.TaskFileWriteFailed,
                reason ?? "任务文件无法写入，审批未生效。"),
            _ => ToolExecutionResult.Fail(DevHubErrorCodes.Internal, $"未知的审批状态：{status}"),
        };
    }

    /// <summary>分页列出问题单。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> ListInputsAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryGetLimit(input, out var limit))
        {
            return InvalidLimit();
        }

        var page = await _inputs.ListAsync(
            GetString(input, "workspaceId"),
            GetString(input, "taskId"),
            GetString(input, "status") ?? "open",
            limit,
            GetString(input, "cursor"),
            cancellationToken).ConfigureAwait(false);

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(page, ToolJson.Options));
    }

    /// <summary>读取问题单详情。</summary>
    /// <param name="input">入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<ToolExecutionResult> GetInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!TryGetRequiredString(input, "id", out var id))
        {
            return ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, "缺少 id。");
        }

        var detail = await _inputs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return detail is null
            ? ToolExecutionResult.Fail("NOT_FOUND", $"问题单不存在：{id}")
            : ToolExecutionResult.Ok(JsonSerializer.Serialize(detail, ToolJson.Options));
    }

    /// <summary>构造 limit 非法错误。</summary>
    private static ToolExecutionResult InvalidLimit() =>
        ToolExecutionResult.Fail(DevHubErrorCodes.InvalidArgument, $"limit 必须在 1–{MaxLimit} 之间。");

    /// <summary>读取可选 limit，缺省 20；越界返回 false。</summary>
    /// <param name="input">入参。</param>
    /// <param name="limit">解析结果。</param>
    private static bool TryGetLimit(JsonElement input, out int limit)
    {
        limit = DefaultLimit;
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("limit", out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out limit))
        {
            return false;
        }

        return limit is >= 1 and <= MaxLimit;
    }

    /// <summary>读取必填字符串。</summary>
    /// <param name="input">入参。</param>
    /// <param name="name">属性名。</param>
    /// <param name="value">解析结果。</param>
    private static bool TryGetRequiredString(JsonElement input, string name, out string value)
    {
        var raw = GetString(input, name);
        value = raw ?? string.Empty;
        return !string.IsNullOrWhiteSpace(raw);
    }

    /// <summary>读取可选字符串；缺失或非字符串返回 null。</summary>
    /// <param name="input">入参。</param>
    /// <param name="name">属性名。</param>
    private static string? GetString(JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>读取可选整数。</summary>
    /// <param name="input">入参。</param>
    /// <param name="name">属性名。</param>
    /// <param name="fallback">缺省值。</param>
    private static int GetInt(JsonElement input, string name, int fallback)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var value))
        {
            return fallback;
        }

        return value;
    }

    /// <summary>读取可选布尔值。</summary>
    /// <param name="input">入参。</param>
    /// <param name="name">属性名。</param>
    /// <param name="fallback">缺省值。</param>
    private static bool GetBool(JsonElement input, string name, bool fallback)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(name, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return fallback;
        }

        return element.GetBoolean();
    }

    /// <summary>读取可选字符串数组；缺失返回 null。</summary>
    /// <param name="input">入参。</param>
    /// <param name="name">属性名。</param>
    private static IReadOnlyList<string>? GetStringArray(JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                values.Add(text);
            }
        }

        return values;
    }
}
