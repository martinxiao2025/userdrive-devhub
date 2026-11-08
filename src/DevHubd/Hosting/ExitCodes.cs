namespace DevHubd.Hosting;

/// <summary>
/// 进程退出码约定（§4.3）。除 0 外均为"启动失败即退出"，不做端口顺延或静默降级。
/// </summary>
public static class ExitCodes
{
    /// <summary>正常退出。</summary>
    public const int Success = 0;

    /// <summary>参数非法。</summary>
    public const int InvalidArguments = 1;

    /// <summary>已有实例在运行。</summary>
    public const int InstanceAlreadyRunning = 2;

    /// <summary>MCP 端口被占用。</summary>
    public const int PortInUse = 3;

    /// <summary>数据目录不可写。</summary>
    public const int DataDirectoryNotWritable = 4;

    /// <summary>数据库迁移失败。</summary>
    public const int MigrationFailed = 5;

    /// <summary>插件包内的角色声明非法（缺字段、取值越界、引用不存在的技能）。</summary>
    public const int InvalidPluginDeclaration = 6;
}
