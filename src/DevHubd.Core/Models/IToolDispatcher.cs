using System.Text.Json;

namespace DevHubd.Core.Models;

/// <summary>
/// 工具执行唯一入口。两条模型路径最终都回到这里，
/// 保证"模型能看到什么、能写哪里"完全由 C# 决定（白名单 + 写作用域 + 预算）。
/// </summary>
public interface IToolDispatcher
{
    /// <summary>本回合已写入的文件（相对写根，斜杠分隔）。</summary>
    IReadOnlyList<string> ChangedFiles { get; }

    /// <summary>本回合已执行的工具调用次数。</summary>
    int ExecutedCalls { get; }

    /// <summary>本回合暴露给模型的工具白名单。</summary>
    IReadOnlyList<ToolDescriptor> Describe();

    /// <summary>执行一次工具调用。越界、超预算等一律以失败结果返回，不抛异常。</summary>
    /// <param name="turnId">回合 id，用于日志关联。</param>
    /// <param name="toolName">工具名。</param>
    /// <param name="input">工具入参。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ToolExecutionResult> ExecuteAsync(
        string turnId,
        string toolName,
        JsonElement input,
        CancellationToken cancellationToken);
}
