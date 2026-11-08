using DevHubd.Core.Models;
using DevHubd.Core.Queue;
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
public sealed record TurnRunOutcome(
    string TurnId,
    string Provider,
    string? Model,
    string DeliveryOutcome,
    TurnResult Result,
    IReadOnlyList<string> Artifacts,
    string? WorkDirectory);

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
        2. 读取范围限定在工作区内；写入范围由系统强制校验，越界会被拒绝。
        3. 当前交付档位为 {0}：{1}
        4. 不要尝试提交、推送、合并或运行命令。
        5. 完成后用简洁中文总结你做了什么；如果没有写入任何文件，请明确说明。
        """;

    private static readonly string[] _sensitivePromptSegments = { ".env", "secrets" };

    private readonly ModelClientRegistry _registry;
    private readonly ExecutionOptions _options;
    private readonly DevHubPaths _paths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TurnRunner> _logger;

    /// <summary>注入模型注册表、执行配置、路径、日志工厂与日志器。</summary>
    /// <param name="registry">模型客户端注册表。</param>
    /// <param name="options">执行配置。</param>
    /// <param name="paths">运行时路径。</param>
    /// <param name="loggerFactory">用于创建工具执行器日志器。</param>
    /// <param name="logger">日志器。</param>
    public TurnRunner(
        ModelClientRegistry registry,
        ExecutionOptions options,
        DevHubPaths paths,
        ILoggerFactory loggerFactory,
        ILogger<TurnRunner> logger)
    {
        _registry = registry;
        _options = options;
        _paths = paths;
        _loggerFactory = loggerFactory;
        _logger = logger;
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

        var deliveryOutcome = ResolveDeliveryOutcome(task);
        var workDirectory = deliveryOutcome == "delivered" ? null : CreateWorkDirectory(item.Id);
        var writeRoot = workDirectory ?? workspace.Root;

        var workspaceContext = new TurnWorkspace(
            workspace.Root,
            writeRoot,
            task.WriteScope,
            EnforceScope: deliveryOutcome == "delivered");

        var budget = _options.ToBudget();
        IToolDispatcher dispatcher = new ToolDispatcher(
            workspaceContext,
            budget,
            _loggerFactory.CreateLogger<ToolDispatcher>());

        var request = new TurnRequest(
            turnId,
            turnId,
            provider,
            item.Id,
            item.WorkspaceId,
            item.TaskId,
            BuildSystemPrompt(deliveryOutcome, task, workspace),
            task.Prompt,
            dispatcher.Describe(),
            budget,
            Model: null,
            TimeSpan.FromSeconds(_options.MaxDurationSeconds));

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

        if (result.State == TurnState.Done)
        {
            _logger.LogInformation(
                "模型回合结束：queueItemId={QueueItemId} attempt={Attempt} turnId={TurnId} provider={Provider} toolCalls={ToolCalls} filesChanged={FilesChanged} " +
                "promptTokens={PromptTokens} completionTokens={CompletionTokens} totalTokens={TotalTokens} usageEstimated={UsageEstimated} usageMissing={UsageMissing}",
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
                result.Usage is null);
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

        return new TurnRunOutcome(turnId, provider, request.Model, deliveryOutcome, result, artifacts, workDirectory);
    }

    /// <summary>
    /// 计算实际交付档位：全局 <c>auto</c> 且任务声明 <c>auto</c> 才交付；任务可收紧但不可放宽。
    /// </summary>
    /// <param name="task">任务文档。</param>
    private string ResolveDeliveryOutcome(TaskDocument task) =>
        string.Equals(_options.DeliveryMode, "auto", StringComparison.Ordinal) &&
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

    /// <summary>构造系统提示词，明确交付档位、写作用域与敏感路径边界。</summary>
    /// <param name="deliveryOutcome">交付档位。</param>
    /// <param name="task">任务文档。</param>
    /// <param name="workspace">工作区。</param>
    private static string BuildSystemPrompt(string deliveryOutcome, TaskDocument task, WorkspaceDefinition workspace)
    {
        var delivery = deliveryOutcome == "delivered"
            ? "auto（可在 writeScope 内直接写工作区，但仍不提交、不推送、不合并）"
            : "dispatch_only（只写隔离目录，结果交人工复核，不改工作区）";

        var scope = task.WriteScope.Count == 0 ? "（未声明，视为只读）" : string.Join(", ", task.WriteScope);
        var sensitive = string.Join(", ", _sensitivePromptSegments);

        return string.Format(
            SystemPromptTemplate,
            delivery,
            $"工作区根为 {workspace.Root}，任务 writeScope 为 {scope}，敏感路径（{sensitive}、.git、*.pem、*.key）一律拒绝。");
    }
}
