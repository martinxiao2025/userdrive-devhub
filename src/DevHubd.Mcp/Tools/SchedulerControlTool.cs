using System.ComponentModel;
using System.Text.Json;
using DevHubd.Mcp.Abstractions;
using DevHubd.Mcp.Contracts;
using DevHubd.Mcp.Errors;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevHubd.Mcp.Tools;

/// <summary>
/// <c>scheduler_control</c>：调度控制面（start / pause / resume / scan_now / set_mode /
/// reset_breaker / probe_models / status），每次动作都写 <c>audit</c>。
/// </summary>
[McpServerToolType]
public sealed class SchedulerControlTool
{
    private readonly ISchedulerControl _control;
    private readonly IToolDigestSource _digest;

    /// <summary>注入控制面与摘要源。</summary>
    /// <param name="control">控制面。</param>
    /// <param name="digest">摘要数据源。</param>
    public SchedulerControlTool(ISchedulerControl control, IToolDigestSource digest)
    {
        _control = control;
        _digest = digest;
    }

    /// <summary>执行一个控制动作并返回动作后的状态快照。</summary>
    /// <param name="action">动作名。</param>
    /// <param name="reason">暂停 / 重置 / 启动的原因。</param>
    /// <param name="mode">set_mode 的目标档位。</param>
    /// <param name="provider">reset_breaker 的目标 provider。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [McpServerTool(Name = "scheduler_control", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("控制调度器：start/pause/resume/scan_now/set_mode/reset_breaker/probe_models/status。动作会写入审计日志。")]
    public async Task<CallToolResult> ControlAsync(
        [Description("动作：start / pause / resume / scan_now / set_mode / reset_breaker / probe_models / status。")]
        string action,
        [Description("可选：操作原因，写入审计记录。")] string? reason = null,
        [Description("set_mode 的目标档位：auto 或 dispatch_only。")] string? mode = null,
        [Description("reset_breaker 的目标 provider：openai 或 vscode。")] string? provider = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = action switch
            {
                "start" => await _control.StartAsync(reason, cancellationToken).ConfigureAwait(false),
                "pause" => await _control.PauseAsync(reason, cancellationToken).ConfigureAwait(false),
                "resume" => await _control.ResumeAsync(cancellationToken).ConfigureAwait(false),
                "scan_now" => await _control.RequestScanAsync(cancellationToken).ConfigureAwait(false),
                "set_mode" => await _control.SetModeAsync(mode ?? string.Empty, cancellationToken).ConfigureAwait(false),
                "reset_breaker" => await _control
                    .ResetBreakerAsync(provider ?? string.Empty, reason, cancellationToken)
                    .ConfigureAwait(false),
                "probe_models" => await _control.ProbeModelsAsync(cancellationToken).ConfigureAwait(false),
                "status" => await _control.GetStateAsync(cancellationToken).ConfigureAwait(false),
                _ => throw new SchedulerControlException(
                    "INVALID_ACTION",
                    $"未知动作：{action}",
                    "使用 start/pause/resume/scan_now/set_mode/reset_breaker/probe_models/status 之一。",
                    retryable: true),
            };

            return await ToolResults
                .SuccessAsync(JsonSerializer.Serialize(snapshot, ToolJson.Options), _digest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SchedulerControlException ex)
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    ex.Code,
                    ex.Message,
                    ex.Retryable,
                    ex.Next,
                    new { action, mode, provider }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return await ToolResults.FailureAsync(
                McpErrors.Envelope(
                    "INVALID_ARGUMENT",
                    ex.Message,
                    retryable: true,
                    next: "检查 mode / provider 取值后重试。",
                    details: new { action, mode, provider }),
                _digest,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>动作后的快照类型（供快照测试引用，保持契约可见）。</summary>
    public static Type SnapshotType => typeof(SchedulerControlSnapshot);
}
