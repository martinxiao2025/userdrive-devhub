namespace Devhubd.Hosting;

/// <summary>
/// 启动选项：端口固定 3091（可用 <c>--port</c> 覆盖），数据目录默认 %LOCALAPPDATA%\userdrive-devhub，
/// 交付档位默认 <c>dispatch_only</c>（需 <c>--allow-auto-delivery</c> 显式解锁）。
/// </summary>
public sealed class DevhubdOptions
{
    /// <summary>锁定的默认 MCP 端口，必须与 <c>plugin/mcp.json</c> 的 URL 保持一致。</summary>
    public const int DefaultPort = 3091;

    /// <summary>绑定地址，仅回环。</summary>
    public string BindAddress { get; private init; } = "127.0.0.1";

    /// <summary>MCP 端口。</summary>
    public int McpPort { get; private init; } = DefaultPort;

    /// <summary>自定义数据目录；null 表示默认目录。</summary>
    public string? DataDirectory { get; private init; }

    /// <summary>是否显式解锁 <c>auto</c> 交付档位。</summary>
    public bool AllowAutoDelivery { get; private init; }

    /// <summary>扫描间隔（秒），P0 仅记录，不启动定时器。</summary>
    public int IntervalSeconds { get; private init; } = 30;

    /// <summary>实际生效的交付档位。</summary>
    public string DeliveryMode => AllowAutoDelivery ? "auto" : "dispatch_only";

    /// <summary>
    /// 解析命令行与环境变量。<c>--port</c>/<c>--data-dir</c>/<c>--interval</c>/<c>--allow-auto-delivery</c>；
    /// 环境变量 <c>DEVHUB_MCP_PORT</c>、<c>DEVHUB_DATA_DIR</c>。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <exception cref="ArgumentException">端口或间隔非法。</exception>
    public static DevhubdOptions FromArgsAndEnvironment(string[] args)
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

        return new DevhubdOptions
        {
            McpPort = port,
            DataDirectory = dataDirectory,
            AllowAutoDelivery = allowAuto,
            IntervalSeconds = interval,
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
