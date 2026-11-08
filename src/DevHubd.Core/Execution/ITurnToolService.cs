using System.Text.Json;
using DevHubd.Core.Models;

namespace DevHubd.Core.Execution;

/// <summary>
/// 后台回合可用的服务层工具（<c>task_upsert</c>、<c>queue_*</c>、<c>result_*</c> 等）。
/// 由 Core 定义端口、由 MCP 层实现，保证依赖方向仍是 <c>Host → Mcp → Core</c>，
/// 同时让后台回合与聊天侧走同一套服务层语义。
/// </summary>
public interface ITurnToolService
{
    /// <summary>服务层工具描述（名称、说明与入参 Schema）。</summary>
    IReadOnlyList<ToolDescriptor> Describe();

    /// <summary>按名执行一个服务层工具；失败以 <see cref="ToolExecutionResult"/> 返回，不抛业务异常。</summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="input">模型给出的 JSON 入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ToolExecutionResult> InvokeAsync(
        string toolName,
        JsonElement input,
        CancellationToken cancellationToken = default);
}
