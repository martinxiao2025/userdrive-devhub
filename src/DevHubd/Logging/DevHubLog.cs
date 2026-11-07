using Microsoft.Extensions.Logging;

namespace DevHubd.Logging;

/// <summary>
/// 进程级日志事件表。全部通过 <see cref="LoggerMessageAttribute"/> 源生成：
/// EventId、级别、消息模板与字段名在编译期固定，既保证结构化输出，也避免运行时反射与字符串拼接。
/// EventId 分段：1xxx 启动与退出、11xx 资源就绪、12xx 健康与发现、13xx 关闭。
/// </summary>
internal static partial class DevHubLog
{
    /// <summary>启动中：记录版本、pid、端点、交付档位与数据目录。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="version">程序集版本。</param>
    /// <param name="pid">当前进程 pid。</param>
    /// <param name="endpoint">MCP 端点 URL。</param>
    /// <param name="deliveryMode">交付档位（auto / dispatch_only）。</param>
    /// <param name="dataDirectory">数据目录绝对路径。</param>
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "devhubd 启动：version={Version} pid={Pid} endpoint={Endpoint} deliveryMode={DeliveryMode} dataDirectory={DataDirectory}")]
    public static partial void Starting(
        ILogger logger,
        string version,
        int pid,
        string endpoint,
        string deliveryMode,
        string dataDirectory);

    /// <summary>命令行参数非法（退出码 1）。</summary>
    [LoggerMessage(EventId = 1001, Level = LogLevel.Error, Message = "启动失败：命令行参数非法（退出码 1）。")]
    public static partial void InvalidArguments(ILogger logger, Exception exception);

    /// <summary>机器级单实例互斥体或锁文件已被占用（退出码 2）。</summary>
    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "启动失败：已有实例在运行（退出码 2）。lockPath={LockPath} hint={Hint}")]
    public static partial void InstanceAlreadyRunning(ILogger logger, string lockPath, string hint);

    /// <summary>MCP 端口已被占用，按约定失败退出而不顺延（退出码 3）。</summary>
    [LoggerMessage(EventId = 1003, Level = LogLevel.Error,
        Message = "启动失败：MCP 端口 {Port} 已被占用（退出码 3，不顺延）。hint={Hint}")]
    public static partial void PortConflict(ILogger logger, int port, string hint);

    /// <summary>数据目录不可写（退出码 4）。</summary>
    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "启动失败：数据目录不可写（退出码 4）。directory={Directory}")]
    public static partial void DataDirectoryNotWritable(ILogger logger, string directory, Exception exception);

    /// <summary>数据库迁移失败（退出码 5）。</summary>
    [LoggerMessage(EventId = 1005, Level = LogLevel.Critical,
        Message = "启动失败：数据库迁移失败（退出码 5）。database={Database}")]
    public static partial void MigrationFailed(ILogger logger, string database, Exception exception);

    /// <summary>数据库迁移完成。</summary>
    [LoggerMessage(EventId = 1100, Level = LogLevel.Information,
        Message = "数据库就绪：{Database}（schema v{SchemaVersion}）")]
    public static partial void DatabaseReady(ILogger logger, string database, int schemaVersion);

    /// <summary>Kestrel 已开始监听 MCP 端点。</summary>
    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "MCP 端点已监听：{Url}")]
    public static partial void Listening(ILogger logger, string url);

    /// <summary>扩展发现文件已写出。</summary>
    [LoggerMessage(EventId = 1102, Level = LogLevel.Debug, Message = "runtime.json 已写出：{Path}")]
    public static partial void RuntimeFileWritten(ILogger logger, string path);

    /// <summary>发现文件写出失败：扩展将无法发现本实例。</summary>
    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning,
        Message = "runtime.json 写出失败，扩展将无法发现本实例：{Path}")]
    public static partial void RuntimeFileWriteFailed(ILogger logger, string path, Exception exception);

    /// <summary>发现文件清理失败；退出流程继续。</summary>
    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning, Message = "runtime.json 清理失败：{Path}")]
    public static partial void RuntimeFileDeleteFailed(ILogger logger, string path, Exception exception);

    /// <summary>执行器配置就绪：只记录环境变量名与是否已配置，绝不记录 key 值。</summary>
    [LoggerMessage(EventId = 1105, Level = LogLevel.Information,
        Message = "执行器已配置：configPath={ConfigPath} provider={Provider} model={Model} apiKeyEnvVar={ApiKeyEnvVar} apiKeyConfigured={ApiKeyConfigured} maxParallel={MaxParallel} deliveryMode={DeliveryMode}")]
    public static partial void ExecutionConfigured(
        ILogger logger,
        string configPath,
        string provider,
        string model,
        string apiKeyEnvVar,
        bool apiKeyConfigured,
        int maxParallel,
        string deliveryMode);

    /// <summary>用户配置存在问题（不阻断启动，但必须可见）。</summary>
    [LoggerMessage(EventId = 1106, Level = LogLevel.Warning, Message = "用户配置问题：{Problem}")]
    public static partial void UserConfigProblem(ILogger logger, string problem);

    /// <summary>健康检查探库失败，<c>/health</c> 返回 503。</summary>
    [LoggerMessage(EventId = 1200, Level = LogLevel.Warning,
        Message = "健康检查失败，/health 返回 503。database={Database}")]
    public static partial void HealthDegraded(ILogger logger, string database, Exception exception);

    /// <summary>扩展确认通知：记录条数，不记录标题正文。</summary>
    [LoggerMessage(EventId = 1201, Level = LogLevel.Debug, Message = "通知已确认：count={Count}")]
    public static partial void NoticeAcked(ILogger logger, int count);

    /// <summary>收到停止信号，开始关闭。</summary>
    [LoggerMessage(EventId = 1300, Level = LogLevel.Information,
        Message = "收到停止信号，正在关闭。uptimeSeconds={UptimeSeconds}")]
    public static partial void Stopping(ILogger logger, long uptimeSeconds);

    /// <summary>收到内部 API 的优雅停止请求。</summary>
    [LoggerMessage(EventId = 1302, Level = LogLevel.Information,
        Message = "收到优雅停止请求（内部 API）。pid={Pid}")]
    public static partial void ShutdownRequested(ILogger logger, int pid);

    /// <summary>进程退出：每条退出路径都必须记录退出码。</summary>
    [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "devhubd 退出：exitCode={ExitCode}")]
    public static partial void Exited(ILogger logger, int exitCode);
}
