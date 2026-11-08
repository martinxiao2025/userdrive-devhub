using DevHubd.Core.Scheduling;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using DevHubd.Scheduling;
using CoreInput = DevHubd.Core.Input;

namespace DevHubd.Input;

/// <summary>
/// 问题单会话侧实现：把 <see cref="CoreInput.InputRequestStore"/> 的持久化结果映射成 MCP 契约，
/// 并在回答成功后请求一次调度唤醒，让被挂起的任务在下一轮 tick 立即恢复。
/// </summary>
public sealed class InputRequestService : IInputRequestService
{
    private readonly CoreInput.InputRequestStore _store;
    private readonly SchedulerStateStore _states;
    private readonly SchedulerWakeSignal _wake;

    /// <summary>注入问题单存取、调度状态与唤醒信号。</summary>
    /// <param name="store">问题单存取。</param>
    /// <param name="states">调度状态存取（读取 nextScanAt）。</param>
    /// <param name="wake">调度唤醒信号。</param>
    public InputRequestService(
        CoreInput.InputRequestStore store,
        SchedulerStateStore states,
        SchedulerWakeSignal wake)
    {
        _store = store;
        _states = states;
        _wake = wake;
    }

    /// <inheritdoc />
    public async Task<InputRequestPageResult> ListAsync(
        string? workspaceId,
        string? taskId,
        string status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var page = await _store
            .ListAsync(workspaceId, taskId, status, limit, cursor, cancellationToken)
            .ConfigureAwait(false);

        return new InputRequestPageResult(page.Items.Select(Map).ToArray(), page.NextCursor);
    }

    /// <inheritdoc />
    public async Task<InputRequestDetailResult?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var detail = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return detail is null ? null : Map(detail);
    }

    /// <inheritdoc />
    public async Task<(InputAnswerStatus Status, InputAnswerResult? Result, string? Reason)> AnswerAsync(
        string id,
        string answer,
        string? optionId,
        string? answeredBy,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _store
            .AnswerAsync(id, answer, optionId, answeredBy, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        switch (outcome.Status)
        {
            case CoreInput.InputAnswerStatus.Answered:
            case CoreInput.InputAnswerStatus.Duplicate:
                _wake.Request();
                var state = await _states.ReadAsync(cancellationToken).ConfigureAwait(false);
                var answered = outcome.Status == CoreInput.InputAnswerStatus.Answered;
                return (
                    answered ? InputAnswerStatus.Answered : InputAnswerStatus.Duplicate,
                    new InputAnswerResult(
                        outcome.RequestId,
                        answered ? "answered" : "duplicate",
                        outcome.ResumedQueueItemId,
                        state.NextScanAt,
                        outcome.AcceptedAt),
                    null);

            case CoreInput.InputAnswerStatus.Conflict:
                return (InputAnswerStatus.Conflict, null, outcome.Reason);

            case CoreInput.InputAnswerStatus.Closed:
                return (InputAnswerStatus.Closed, null, outcome.Reason);

            case CoreInput.InputAnswerStatus.NotFound:
                return (InputAnswerStatus.NotFound, null, outcome.Reason);

            default:
                return (InputAnswerStatus.InvalidAnswer, null, outcome.Reason);
        }
    }

    /// <inheritdoc />
    public async Task<(InputCancelStatus Status, InputCancelResult? Result, string? Reason)> CancelAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _store.CancelAsync(id, reason, cancellationToken).ConfigureAwait(false);

        return outcome.Status switch
        {
            CoreInput.InputCancelStatus.Cancelled => (
                InputCancelStatus.Cancelled,
                new InputCancelResult(outcome.RequestId, "cancelled", outcome.QueueItemId, outcome.CancelledAt ?? string.Empty),
                null),
            CoreInput.InputCancelStatus.NotFound => (InputCancelStatus.NotFound, null, outcome.Reason),
            _ => (InputCancelStatus.Closed, null, outcome.Reason),
        };
    }

    /// <summary>列表行映射。</summary>
    /// <param name="row">持久化行。</param>
    private static InputRequestSummary Map(CoreInput.InputRequestRow row) => new(
        row.Id,
        row.WorkspaceId,
        row.TaskId,
        row.QueueItemId,
        row.RoleId,
        row.Status,
        row.Question,
        row.MissingInputs,
        row.OptionCount,
        row.CreatedAt,
        row.ExpiresAt,
        row.AnsweredAt);

    /// <summary>详情映射。</summary>
    /// <param name="detail">持久化详情。</param>
    private static InputRequestDetailResult Map(CoreInput.InputRequestDetail detail) => new(
        detail.Id,
        detail.WorkspaceId,
        detail.TaskId,
        detail.QueueItemId,
        detail.RoleId,
        detail.Status,
        detail.Question,
        detail.Context,
        detail.Options.Select(option => new InputOptionResult(option.Id, option.Label, option.Value, option.Recommended)).ToArray(),
        detail.MissingInputs,
        detail.AllowFreeText,
        detail.CreatedAt,
        detail.ExpiresAt,
        detail.AnsweredAt,
        detail.Answer,
        detail.AnswerOptionId,
        detail.AnswerSource,
        detail.AnsweredBy,
        detail.Related.Select(item => new InputRelatedResult(item.Id, item.Status, item.Question, item.CreatedAt)).ToArray());
}
