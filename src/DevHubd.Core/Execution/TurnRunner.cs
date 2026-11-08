using System.Text;
using System.Text.Json;
using DevHubd.Core.Input;
using DevHubd.Core.Models;
using DevHubd.Core.Queue;
using DevHubd.Core.Roles;
using DevHubd.Core.Scheduling;
using DevHubd.Core.Tasks;
using DevHubd.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace DevHubd.Core.Execution;

/// <summary>一次回合执行的产出（含日志关联键与交付档位）。</summary>
/// <param name="TurnId">回合 id。</param>
/// <param name="Provider">provider。</param>
/// <param name="Model">模型名。</param>
/// <param name="DeliveryOutcome">delivered / awaiting_review。</param>
/// <param name="Result">回合结果。</param>
/// <param name="Artifacts">产物（相对路径）。</param>
/// <param name="WorkDirectory">隔离工作目录；auto 档位为 null。</param>
/// <param name="InputRequestId">本回合写入的问题单 id；null 表示未提问。</param>
public sealed record TurnRunOutcome(
    string TurnId,
    string Provider,
    string? Model,
    string DeliveryOutcome,
    TurnResult Result,
    IReadOnlyList<string> Artifacts,
    string? WorkDirectory,
    string? InputRequestId = null);

/// <summary>
/// 回合执行器：解析交付档位、准备隔离目录、构造工具白名单与提示词、调用模型客户端。
/// 只负责"跑完一个回合"，不写队列终态（由 <see cref="ExecutionCoordinator"/> 统一落库）。
/// </summary>
public sealed class TurnRunner
{
    private const string SystemPromptTemplate =
        """
        你是 devhubd 的定时任务执行器。请严格按任务指令完成工作，并遵守以下约束：
        1. 只能使用提供的工具；工具入参路径必须是相对路径。
        2. {0}
        3. 当前交付档位为 {1}
        4. 本回合可用工具为 {2}；名单之外的工具一律拒绝并返回 TOOL_NOT_ALLOWED。
        5. 不要尝试提交、推送、合并或运行命令。
        6. 完成后用简洁中文总结你做了什么；如果没有写入任何文件，请明确说明。
        """;

    /// <summary>回答段总长度上限（字符）。超出时优先丢弃更早的回答。</summary>
    private const int MaxAnswerPromptChars = 8000;

    /// <summary>单条回答的问题文本截断上限（字符）。</summary>
    private const int MaxAnswerQuestionChars = 500;

    /// <summary>单条回答的回答文本截断上限（字符）。</summary>
    private const int MaxAnswerTextChars = 1000;

    private static readonly string[] _sensitivePromptSegments = { ".env", "secrets" };

    /// <summary>队列项未声明角色时使用的显式缺省角色。</summary>
    public const string DefaultRoleId = "implementer";

    /// <summary>角色快照缺失或未声明时的显式默认：单回合模型轮次上限。</summary>
    public const int DefaultMaxRounds = 12;

    /// <summary>角色快照缺失或未声明时的显式默认：单回合工具调用上限。</summary>
    public const int DefaultMaxToolCalls = 60;

    /// <summary>角色快照缺失或未声明时的显式默认：单回合墙钟上限（秒）。</summary>
    public const int DefaultMaxDurationSeconds = 1800;

    /// <summary>角色快照缺失或未声明时的显式默认：该角色同时运行的回合上限。</summary>
    public const int DefaultMaxParallel = 1;

    /// <summary>
    /// 缺省角色的显式工具集：角色快照缺失（如未部署插件包）时使用，不做隐式兜底。
    /// 仍受 <c>canRequestInput</c> 影响，<c>ask_input</c> 会在不允许提问时被过滤。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultToolNames = new[]
    {
        "read_file",
        "list_dir",
        "search",
        "write_file",
        "apply_patch",
        "ask_input",
        "scheduler_status",
        "queue_list",
        "queue_get",
        "result_list",
        "result_ack",
    };

    private readonly ModelClientRegistry _registry;
    private readonly ExecutionOptions _options;
    private readonly SchedulerStateStore _stateStore;
    private readonly DevHubPaths _paths;
    private readonly InputRequestStore _inputRequests;
    private readonly RoleSnapshotStore _roleSnapshots;
    private readonly ITurnToolService? _turnTools;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TurnRunner> _logger;

    /// <summary>注入模型注册表、执行配置、调度状态存取、路径、问题单存取、角色快照、日志工厂与日志器。</summary>
    /// <param name="registry">模型客户端注册表。</param>
    /// <param name="options">执行配置。</param>
    /// <param name="stateStore">调度状态存取；交付档位每回合从库读取，避免进程内存漂移。</param>
    /// <param name="paths">运行时路径。</param>
    /// <param name="inputRequests">问题单存取。</param>
    /// <param name="roleSnapshots">角色快照（判断 <c>canRequestInput</c> 与工具声明）。</param>
    /// <param name="loggerFactory">用于创建工具执行器日志器。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="turnTools">服务层工具执行端口（<c>task_upsert</c>、<c>queue_*</c> 等）；null 表示无服务层工具。</param>
    public TurnRunner(
        ModelClientRegistry registry,
        ExecutionOptions options,
        SchedulerStateStore stateStore,
        DevHubPaths paths,
        InputRequestStore inputRequests,
        RoleSnapshotStore roleSnapshots,
        ILoggerFactory loggerFactory,
        ILogger<TurnRunner> logger,
        ITurnToolService? turnTools = null)
    {
        _registry = registry;
        _options = options;
        _stateStore = stateStore;
        _paths = paths;
        _inputRequests = inputRequests;
        _roleSnapshots = roleSnapshots;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _turnTools = turnTools;
    }

    /// <summary>
    /// 执行一个回合：超时、取消与异常都转换为 <see cref="TurnResult"/>，不向外抛业务异常。
    /// </summary>
    /// <param name="item">已认领的队列项。</param>
    /// <param name="task">任务文档。</param>
    /// <param name="workspace">所属工作区。</param>
    /// <param name="cancellationToken">停机令牌。</param>
    public async Task<TurnRunOutcome> RunAsync(
        QueueItem item,
        TaskDocument task,
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var provider = item.PreferredProvider;
        var client = _registry.Get(provider);

        if (client is null)
        {
            _logger.LogWarning(
                "provider 未注册，回合无法开始：queueItemId={QueueItemId} attempt={Attempt} provider={Provider} turnId={TurnId}",
                item.Id,
                item.Attempt,
                provider,
                turnId);

            return new TurnRunOutcome(
                turnId,
                provider,
                null,
                "awaiting_review",
                TurnResult.Failed(new ModelError(
                    ModelErrorKinds.BridgeDown,
                    "PROVIDER_NOT_REGISTERED",
                    $"provider 未注册：{provider}")),
                Array.Empty<string>(),
                null);
        }

        // 交付档位以库为准：每回合读取 scheduler_state.mode，控制面改档后下一次判定立即生效。
        // 角色先加载：交付判定要把角色 requiresApproval 纳入，回合预算也由角色声明决定。
        var schedulerState = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        var roleId = string.IsNullOrWhiteSpace(item.RoleId) ? DefaultRoleId : item.RoleId;
        var role = await _roleSnapshots.GetAsync(roleId, cancellationToken).ConfigureAwait(false);
        var deliveryOutcome = ResolveDeliveryOutcome(task, schedulerState.Mode, role);
        var workDirectory = deliveryOutcome == "delivered" ? null : CreateWorkDirectory(item.Id);

        // 读根固定为工作区（只读），写根由交付档位决定：
        // dispatch_only 写该队列项的隔离目录，auto 写工作区并受 writeScope 约束。
        var writeRoot = workDirectory ?? workspace.Root;
        var workspaceContext = new TurnWorkspace(
            workspace.Root,
            writeRoot,
            task.WriteScope,
            EnforceScope: deliveryOutcome == "delivered");

        var budget = BuildBudget(role);
        var canRequestInput = role?.CanRequestInput ?? false;
        var allowedTools = ResolveAllowedTools(roleId, role);

        var dispatcher = new ToolDispatcher(
            workspaceContext,
            budget,
            allowedTools,
            _loggerFactory.CreateLogger<ToolDispatcher>(),
            canRequestInput,
            canRequestInput ? (prompt, token) => AskInputAsync(item, roleId, prompt, token) : null,
            _turnTools);

        // 恢复回合：把同一队列项已回答的问题单结构化追加到原任务指令之后，
        // 让模型看得到人工答案，不再就已回答的问题重复 ask_input。
        var answeredInputs = await _inputRequests
            .ListAnsweredForQueueItemAsync(item.Id, cancellationToken)
            .ConfigureAwait(false);
        var prompt = BuildPromptWithAnswers(task.Prompt, answeredInputs);
        if (answeredInputs.Count > 0)
        {
            _logger.LogInformation(
                "恢复回合注入已回答问题：queueItemId={QueueItemId} attempt={Attempt} answers={AnswerCount} promptChars={PromptChars}",
                item.Id,
                item.Attempt,
                answeredInputs.Count,
                prompt.Length);
        }

        var request = new TurnRequest(
            turnId,
            turnId,
            provider,
            item.Id,
            item.WorkspaceId,
            item.TaskId,
            BuildSystemPrompt(deliveryOutcome, task, workspace, writeRoot, allowedTools),
            prompt,
            dispatcher.Describe(),
            budget,
            Model: null,
            budget.MaxDuration);

        _logger.LogInformation(
            "模型回合开始：queueItemId={QueueItemId} attempt={Attempt} turnId={TurnId} provider={Provider} delivery={Delivery}",
            item.Id,
            item.Attempt,
            turnId,
            provider,
            deliveryOutcome);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(budget.MaxDuration);

        TurnResult result;
        try
        {
            result = await client.RunTurnAsync(request, dispatcher, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = TurnResult.Cancelled(new ModelError(
                ModelErrorKinds.UserCancelled,
                "SHUTDOWN",
                "进程停机，回合已取消。"));
        }
        catch (OperationCanceledException)
        {
            result = TurnResult.Cancelled(new ModelError(
                ModelErrorKinds.BudgetExceeded,
                "TURN_TIMEOUT",
                $"回合超过 {budget.MaxDuration.TotalSeconds:0} 秒，已取消。"));
        }
        catch (Exception exception)
        {
            // 不允许吞异常：记录完整上下文后归类为模型错误，交给上层按有限重试处理。
            _logger.LogError(
                exception,
                "模型回合异常结束：queueItemId={QueueItemId} attempt={Attempt} turnId={TurnId} provider={Provider}",
                item.Id,
                item.Attempt,
                turnId,
                provider);

            result = TurnResult.Failed(new ModelError(
                ModelErrorKinds.ModelError,
                "TURN_EXCEPTION",
                $"回合异常：{exception.Message}"));
        }

        var artifacts = BuildArtifacts(item.Id, workDirectory, dispatcher.ChangedFiles);

        // 提问即暂停：回合正常结束后若模型已写入问题单，则本回合按 needs_input 收尾，
        // token 与工具调用次数照实保留（不估算、不吞成本），由协调器挂起队列项。
        string? inputRequestId = null;
        if (result.State == TurnState.Done && dispatcher.InputRequestId is { } requested)
        {
            inputRequestId = requested;
            _logger.LogInformation(
                "模型回合请求输入，转入挂起：queueItemId={QueueItemId} attempt={Attempt} turnId={TurnId} requestId={RequestId}",
                item.Id,
                item.Attempt,
                turnId,
                requested);

            result = TurnResult.Failed(
                new ModelError(
                    ModelErrorKinds.NeedsInput,
                    DevHubErrorCodes.NeedsInput,
                    dispatcher.InputQuestion ?? "任务缺少必要输入，等待回答。"),
                result.ToolCalls,
                result.Usage) with
            {
                // 提问不是模型失败：保留已解析的模型名与用量，attempt 记录仍可追溯。
                Model = result.Model ?? request.Model,
            };
        }

        if (result.State == TurnState.Done)
        {
            _logger.LogInformation(
                "模型回合结束：queueItemId={QueueItemId} attempt={Attempt} turnId={TurnId} provider={Provider} toolCalls={ToolCalls} filesChanged={FilesChanged} " +
                "promptTokens={PromptTokens} completionTokens={CompletionTokens} totalTokens={TotalTokens} usageEstimated={UsageEstimated} usageMissing={UsageMissing} roundsExhausted={RoundsExhausted}",
                item.Id,
                item.Attempt,
                turnId,
                provider,
                result.ToolCalls,
                artifacts.Count,
                result.Usage?.InputTokens ?? 0,
                result.Usage?.OutputTokens ?? 0,
                result.Usage is null ? 0 : result.Usage.InputTokens + result.Usage.OutputTokens,
                result.Usage?.Estimated ?? false,
                result.Usage is null,
                result.RoundsExhausted);
        }
        else
        {
            _logger.LogWarning(
                "模型回合失败：queueItemId={QueueItemId} attempt={Attempt} turnId={TurnId} provider={Provider} state={State} errorKind={ErrorKind} errorCode={ErrorCode}",
                item.Id,
                item.Attempt,
                turnId,
                provider,
                result.State,
                result.Error?.Kind,
                result.Error?.Code);
        }

        return new TurnRunOutcome(
            turnId,
            provider,
            result.Model ?? request.Model,
            deliveryOutcome,
            result,
            artifacts,
            workDirectory,
            inputRequestId);
    }

    /// <summary>
    /// 解析角色的工具声明；角色快照缺失时使用显式的缺省工具集。
    /// 角色声明为空（历史数据）同样回落到缺省集合并记录警告，不让回合变成"零工具"。
    /// </summary>
    /// <param name="roleId">角色 id。</param>
    /// <param name="role">角色快照；null 表示未加载。</param>
    private IReadOnlyList<string> ResolveAllowedTools(string roleId, Data.Entities.RoleViewEntity? role)
    {
        if (role is null)
        {
            _logger.LogWarning(
                "角色快照缺失，使用显式缺省工具集：role={RoleId} tools={ToolCount}",
                roleId,
                DefaultToolNames.Count);
            return DefaultToolNames;
        }

        var tools = new List<string>();
        try
        {
            var declared = JsonSerializer.Deserialize<string[]>(role.Tools, JsonSerializerOptions.Web);
            if (declared is not null && declared.Length > 0)
            {
                tools.AddRange(declared.Where(tool => !string.IsNullOrWhiteSpace(tool)));
            }
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "角色工具声明解析失败，使用显式缺省工具集：role={RoleId}",
                roleId);
        }

        if (tools.Count == 0)
        {
            _logger.LogWarning(
                "角色未声明任何工具，使用显式缺省工具集：role={RoleId} tools={ToolCount}",
                roleId,
                DefaultToolNames.Count);
            return DefaultToolNames;
        }

        return tools;
    }

    /// <summary>把模型侧提问落为问题单；同任务已有 open 请求时复用既有 id。</summary>
    /// <param name="item">当前队列项。</param>
    /// <param name="roleId">提问时生效的角色 id。</param>
    /// <param name="prompt">已校验的提问内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<InputAcceptance> AskInputAsync(
        QueueItem item,
        string roleId,
        InputPrompt prompt,
        CancellationToken cancellationToken)
    {
        var result = await _inputRequests.CreateAsync(
            new InputRequestDraft(
                item.WorkspaceId,
                item.TaskId,
                item.Id,
                roleId,
                item.TaskHash,
                prompt.Question,
                prompt.Context is null ? null : JsonSerializer.Serialize(new { summary = prompt.Context }, JsonSerializerOptions.Web),
                prompt.Options.Count == 0 ? null : JsonSerializer.Serialize(prompt.Options, JsonSerializerOptions.Web),
                prompt.MissingInputs.Count == 0 ? null : JsonSerializer.Serialize(prompt.MissingInputs, JsonSerializerOptions.Web),
                AllowFreeText: true,
                DateTimeOffset.UtcNow.AddHours(_options.InputTimeoutHours)),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "问题单已写入：queueItemId={QueueItemId} requestId={RequestId} created={Created} role={RoleId}",
            item.Id,
            result.RequestId,
            result.Created,
            roleId);

        return InputAcceptance.Ok(result.RequestId, result.Created);
    }

    /// <summary>
    /// 把角色声明转换为回合预算：maxRounds → 模型轮次，maxToolCalls → 工具调用总数，
    /// maxDurationSeconds → 墙钟上限。角色字段未声明时用显式默认值；角色快照整体缺失时沿用全局配置。
    /// 写入数、字节数等文件预算仍取全局配置。
    /// </summary>
    /// <param name="role">角色快照；null 表示未加载。</param>
    private BudgetLimits BuildBudget(Data.Entities.RoleViewEntity? role)
    {
        // 角色快照缺失时沿用全局配置（本身就是显式配置），不凭空替换默认值。
        if (role is null)
        {
            return _options.ToBudget();
        }

        var maxRounds = role.MaxRounds > 0 ? role.MaxRounds : DefaultMaxRounds;
        var maxToolCalls = role.MaxToolCalls > 0 ? role.MaxToolCalls : DefaultMaxToolCalls;
        var maxDurationSeconds = role.MaxDurationSeconds > 0
            ? role.MaxDurationSeconds
            : DefaultMaxDurationSeconds;

        return _options.ToBudget() with
        {
            MaxToolRounds = maxRounds,
            MaxToolCalls = maxToolCalls,
            MaxDuration = TimeSpan.FromSeconds(maxDurationSeconds),
        };
    }

    /// <summary>
    /// 解析交付档位：任务显式 <c>requiresApproval</c>、角色声明必须审批、全局档位或任务档位
    /// 任一不是 <c>auto</c> 时进入待审。任务级与角色级声明都优先于全局档位，避免全局 auto 绕过人工闸门。
    /// </summary>
    /// <param name="task">任务文档。</param>
    /// <param name="mode">库中的当前交付档位（<c>scheduler_state.mode</c>）。</param>
    /// <param name="role">角色快照；null 表示未加载。</param>
    private static string ResolveDeliveryOutcome(TaskDocument task, string mode, Data.Entities.RoleViewEntity? role) =>
        !task.RequiresApproval &&
        role?.RequiresApproval != true &&
        string.Equals(mode, "auto", StringComparison.Ordinal) &&
        string.Equals(task.Delivery, "auto", StringComparison.Ordinal)
            ? "delivered"
            : "awaiting_review";

    /// <summary>创建隔离工作目录：<c>%LOCALAPPDATA%\userdrive-devhub\work\&lt;queueItemId&gt;</c>。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    private string CreateWorkDirectory(string queueItemId)
    {
        var directory = Path.Combine(_paths.DataDirectory, "work", queueItemId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>把工具写入的相对路径映射为对外的产物路径。</summary>
    /// <param name="queueItemId">队列项 id。</param>
    /// <param name="workDirectory">隔离目录；auto 档位为 null。</param>
    /// <param name="changedFiles">工具写入的相对路径。</param>
    private static IReadOnlyList<string> BuildArtifacts(
        string queueItemId,
        string? workDirectory,
        IReadOnlyList<string> changedFiles)
    {
        if (workDirectory is null)
        {
            return changedFiles;
        }

        return changedFiles
            .Select(file => $"work/{queueItemId}/{file}")
            .ToArray();
    }

    /// <summary>构造系统提示词，明确读根、写根、交付档位与敏感路径边界。</summary>
    /// <param name="deliveryOutcome">交付档位：delivered / awaiting_review。</param>
    /// <param name="task">任务文档。</param>
    /// <param name="workspace">工作区（读根）。</param>
    /// <param name="writeRoot">本回合写根：auto 为工作区根，dispatch_only 为隔离工作目录。</param>
    /// <param name="allowedTools">角色声明的工具名集合。</param>
    private static string BuildSystemPrompt(
        string deliveryOutcome,
        TaskDocument task,
        WorkspaceDefinition workspace,
        string writeRoot,
        IReadOnlyList<string> allowedTools)
    {
        var delivered = deliveryOutcome == "delivered";
        var delivery = delivered
            ? "auto（可在 writeScope 内直接写工作区，结果直接交付，但仍不提交、不推送、不合并）"
            : "dispatch_only（模型照常执行回合，但写入只落该队列项的隔离目录，结果进入 awaiting_review 等人工复核，不改工作区）";

        var scope = task.WriteScope.Count == 0 ? "（未声明，视为只读）" : string.Join(", ", task.WriteScope);
        var sensitive = string.Join(", ", _sensitivePromptSegments);
        var roots = delivered
            ? $"读根与写根同为工作区 {workspace.Root}：读工具只能读工作区，写工具只能在任务 writeScope（{scope}）内写工作区"
            : $"读根为工作区 {workspace.Root}（只读），可用 read_file / list_dir / search 读取源码与既有文件；" +
              $"写根为隔离工作目录 {writeRoot}，write_file / apply_patch 只能写该目录，读工具看不到隔离目录中的产物";

        return string.Format(
            SystemPromptTemplate,
            $"{roots}；敏感路径（{sensitive}、.git、*.pem、*.key）一律拒绝，越界由进程强制拦截。",
            delivery,
            string.Join(", ", allowedTools));
    }

    /// <summary>
    /// 把已回答的问题单追加到任务指令之后。多个问题按提问时间升序；超出总长度预算时
    /// 优先保留最近的问题，并显式标注被省略的条数。
    /// </summary>
    /// <param name="prompt">原任务指令。</param>
    /// <param name="answers">已回答的问题单（已按提问时间升序）。</param>
    private static string BuildPromptWithAnswers(string prompt, IReadOnlyList<AnsweredInput> answers)
    {
        if (answers.Count == 0)
        {
            return prompt;
        }

        // 由新到旧纳入，保证预算紧张时保留最近、最相关的回答。
        var kept = new List<string>();
        var used = 0;
        var dropped = 0;
        for (var index = answers.Count - 1; index >= 0; index--)
        {
            var section = FormatAnsweredInput(answers[index], index + 1);
            if (kept.Count > 0 && used + section.Length > MaxAnswerPromptChars)
            {
                dropped = index + 1;
                break;
            }

            kept.Add(section);
            used += section.Length;
        }

        kept.Reverse();

        var builder = new StringBuilder(prompt);
        builder.AppendLine().AppendLine();
        builder.AppendLine("## 此前的人工回答");
        builder.AppendLine();
        builder.AppendLine("系统已把你在之前回合提出的问题与用户回答附在下面（按提问时间排序）。请直接采用这些回答继续执行，不要就已回答的问题再次调用 ask_input。");
        builder.AppendLine();
        if (dropped > 0)
        {
            builder.AppendLine($"（更早的 {dropped} 条回答因长度限制已省略。）");
            builder.AppendLine();
        }

        foreach (var section in kept)
        {
            builder.AppendLine(section);
        }

        return builder.ToString();
    }

    /// <summary>格式化单条已回答问题：问题、选项、回答、回答时间，各字段按上限截断。</summary>
    /// <param name="answer">已回答问题。</param>
    /// <param name="ordinal">在提示词中的序号（按提问时间）。</param>
    private static string FormatAnsweredInput(AnsweredInput answer, int ordinal)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"### 回答 {ordinal}（提问于 {answer.CreatedAt}）");
        builder.AppendLine($"问题：{TruncateAnswerText(answer.Question, MaxAnswerQuestionChars)}");

        if (answer.Options.Count > 0)
        {
            var labels = answer.Options.Select(option =>
                option.Recommended ? $"{option.Label}（推荐）" : option.Label);
            builder.AppendLine($"选项：{string.Join("；", labels)}");
        }

        var text = string.IsNullOrWhiteSpace(answer.Answer) ? "（未填写文本）" : answer.Answer;
        var chosen = string.IsNullOrWhiteSpace(answer.AnswerOptionId)
            ? string.Empty
            : $"（选中选项：{answer.AnswerOptionId}）";
        builder.AppendLine($"用户回答：{TruncateAnswerText(text, MaxAnswerTextChars)}{chosen}");

        var answeredBy = string.IsNullOrWhiteSpace(answer.AnsweredBy) ? string.Empty : $"（{answer.AnsweredBy}）";
        builder.AppendLine($"回答时间：{answer.AnsweredAt ?? "（未知）"}{answeredBy}");
        return builder.ToString();
    }

    /// <summary>超长文本截断：保留前 <paramref name="maxLength"/> 字符并追加截断标记。</summary>
    /// <param name="value">原文。</param>
    /// <param name="maxLength">上限字符数。</param>
    private static string TruncateAnswerText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return string.Concat(value.AsSpan(0, maxLength), "…（截断）");
    }
}
