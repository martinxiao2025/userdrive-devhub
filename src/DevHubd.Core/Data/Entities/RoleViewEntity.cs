namespace DevHubd.Core.Data.Entities;

/// <summary>
/// <c>role_view</c> 的持久化模型：角色声明的数据库快照。
/// 角色文件是唯一来源，本表只保证「状态在持久化层」——进程重启或多消费者读取时以库为准。
/// </summary>
public sealed class RoleViewEntity
{
    /// <summary>角色标识，与文件名一致。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>展示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>能力描述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>声明文件绝对路径。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>声明文件字节的 SHA-256（小写十六进制）。</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>绑定的技能目录名（JSON 数组）。</summary>
    public string Skills { get; set; } = string.Empty;

    /// <summary>可用 MCP 工具名（JSON 数组）。</summary>
    public string Tools { get; set; } = string.Empty;

    /// <summary>交付上限：auto / dispatch_only。</summary>
    public string Delivery { get; set; } = string.Empty;

    /// <summary>是否必须人工审批。</summary>
    public bool RequiresApproval { get; set; }

    /// <summary>是否允许发起提问。</summary>
    public bool CanRequestInput { get; set; }

    /// <summary>该角色同时运行的回合上限。</summary>
    public int MaxParallel { get; set; }

    /// <summary>该角色自动重试上限。</summary>
    public int MaxAttempts { get; set; }

    /// <summary>单回合模型轮次上限。</summary>
    public int MaxRounds { get; set; }

    /// <summary>单回合工具调用上限。</summary>
    public int MaxToolCalls { get; set; }

    /// <summary>单回合墙钟上限（秒）。</summary>
    public int MaxDurationSeconds { get; set; }

    /// <summary>单回合输入 token 软上限；null 表示不限制。</summary>
    public int? MaxInputTokens { get; set; }

    /// <summary>原始 JSON 文本。</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>首次写入时间（ISO-8601）。</summary>
    public string LoadedAt { get; set; } = string.Empty;

    /// <summary>最后更新时间（ISO-8601）。</summary>
    public string UpdatedAt { get; set; } = string.Empty;
}
