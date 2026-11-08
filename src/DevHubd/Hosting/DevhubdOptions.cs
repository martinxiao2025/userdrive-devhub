namespace DevHubd.Hosting;

/// <summary>
/// 启动选项：端口固定 3091（可用 <c>--port</c> 覆盖），数据目录默认 %LOCALAPPDATA%\userdrive-devhub。
/// 交付档位由用户配置的 <c>Execution:DeliveryMode</c> 决定；<c>--allow-auto-delivery</c>
/// 只用于解锁 <c>scheduler_control set_mode auto</c>，不参与上报。
/// </summary>
public sealed class DevHubdOptions
{
    /// <summary>锁定的默认 MCP 端口，必须与 <c>plugin/mcp.json</c> 的 URL 保持一致。</summary>
    public const int DefaultPort = 3091;

    /// <summary>绑定地址，仅回环。</summary>
    public string BindAddress { get; private init; } = "127.0.0.1";

    /// <summary>MCP 端口。</summary>
    public int McpPort { get; private init; } = DefaultPort;

    /// <summary>自定义数据目录；null 表示默认目录。</summary>
    public string? DataDirectory { get; private init; }

    /// <summary>
    /// 是否显式解锁 <c>auto</c> 交付档位；仅作为 <c>scheduler_control set_mode</c> 的闸门，
    /// 不参与上报（实际档位以 <c>Execution:DeliveryMode</c> 为准）。
    /// </summary>
    public bool AllowAutoDelivery { get; private init; }

    /// <summary>默认扫描间隔（秒）；数据库中的 <c>scheduler_state.interval_seconds</c> 优先。</summary>
    public int IntervalSeconds { get; private init; } = 30;

    /// <summary>
    /// 调度总开关：<c>DEVHUB_SCHEDULER=off</c> 时后台循环不扫描、控制面拒绝 start/resume，
    /// 用于"只保留 MCP 观测、不自动派发"的运维场景。
    /// </summary>
    public bool SchedulerEnabled { get; private init; } = true;

    /// <summary>
    /// 解析命令行与环境变量。<c>--port</c>/<c>--data-dir</c>/<c>--interval</c>/<c>--allow-auto-delivery</c>；
    /// 环境变量 <c>DEVHUB_MCP_PORT</c>、<c>DEVHUB_DATA_DIR</c>。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <exception cref="ArgumentException">端口或间隔非法。</exception>
    public static DevHubdOptions FromArgsAndEnvironment(string[] args)
    {
        var port = ReadIntEnvironment("DEVHUB_MCP_PORT") ?? DefaultPort;
        var dataDirectory = Environment.GetEnvironmentVariable("DEVHUB_DATA_DIR");
        var allowAuto = false;
        var interval = 30;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    port = ParseRequiredInt(args, ref i, "--port");
                    break;
                case "--data-dir":
                    dataDirectory = ParseRequiredValue(args, ref i, "--data-dir");
                    break;
                case "--interval":
                    interval = ParseRequiredInt(args, ref i, "--interval");
                    break;
                case "--allow-auto-delivery":
                    allowAuto = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[i]}", nameof(args));
            }
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentException($"端口非法：{port}（允许 1-65535）", nameof(args));
        }

        if (interval < 5)
        {
            throw new ArgumentException($"扫描间隔非法：{interval}（最小 5 秒）", nameof(args));
        }

        return new DevHubdOptions
        {
            McpPort = port,
            DataDirectory = dataDirectory,
            AllowAutoDelivery = allowAuto,
            IntervalSeconds = interval,
            SchedulerEnabled = !string.Equals(
                Environment.GetEnvironmentVariable("DEVHUB_SCHEDULER"),
                "off",
                StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>读取整型环境变量，非法或缺省返回 null。</summary>
    private static int? ReadIntEnvironment(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) ? value : null;
    }

    /// <summary>读取 <c>--key value</c> 形式的值。</summary>
    private static string ParseRequiredValue(string[] args, ref int index, string key)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"参数 {key} 缺少取值", nameof(args));
        }

        index++;
        return args[index];
    }

    /// <summary>读取 <c>--key value</c> 形式的整数取值。</summary>
    private static int ParseRequiredInt(string[] args, ref int index, string key)
    {
        var raw = ParseRequiredValue(args, ref index, key);
        return int.TryParse(raw, out var value)
            ? value
            : throw new ArgumentException($"参数 {key} 需要整数，实际为：{raw}", nameof(args));
    }
}
