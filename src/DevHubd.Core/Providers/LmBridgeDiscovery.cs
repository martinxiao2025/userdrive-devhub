using System.Text.Json;

namespace DevHubd.Core.Providers;

/// <summary>
/// VS Code 扩展写出的 <c>lm-bridge.json</c> 内容。
/// 只包含发现信息，令牌由扩展每次激活重新生成，C# 侧只在内存中使用、不落库、不写日志。
/// </summary>
/// <param name="Pid">扩展宿主进程 pid。</param>
/// <param name="Port">回合桥监听的随机回环端口。</param>
/// <param name="Token">一次性访问令牌。</param>
/// <param name="Model">扩展当前选用的模型名；null 表示由扩展自行决定。</param>
/// <param name="StartedAt">桥启动时间。</param>
public sealed record LmBridgeDiscovery(
    int Pid,
    int Port,
    string Token,
    string? Model,
    string? StartedAt)
{
    /// <summary>发现文件按 camelCase 读取（与扩展写出格式一致）。</summary>
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 读取并校验发现文件。文件缺失、JSON 损坏或字段缺失都返回 false，
    /// 由调用方按 <c>bridge_down</c> 处理，而不是抛异常打断调度。
    /// </summary>
    /// <param name="path">发现文件绝对路径。</param>
    /// <param name="discovery">解析结果。</param>
    /// <param name="error">失败原因（中文，可直接进日志/状态详情）。</param>
    public static bool TryRead(string path, out LmBridgeDiscovery? discovery, out string? error)
    {
        discovery = null;
        error = null;

        if (!File.Exists(path))
        {
            error = "未发现 lm-bridge.json（VS Code 扩展未激活或未安装）";
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<LmBridgePayload>(File.ReadAllText(path), _json);
            if (payload is null || payload.Pid <= 0 || payload.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(payload.Token))
            {
                error = "lm-bridge.json 内容不完整（pid / port / token 缺失）";
                return false;
            }

            discovery = new LmBridgeDiscovery(payload.Pid, payload.Port, payload.Token, payload.Model, payload.StartedAt);
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"lm-bridge.json 读取失败：{exception.GetType().Name}";
            return false;
        }
    }

    /// <summary>发现文件的 JSON 形状（camelCase）。</summary>
    /// <param name="Pid">宿主进程 pid。</param>
    /// <param name="Port">桥端口。</param>
    /// <param name="Token">访问令牌。</param>
    /// <param name="Model">模型名。</param>
    /// <param name="StartedAt">启动时间。</param>
    private sealed record LmBridgePayload(int Pid, int Port, string Token, string? Model, string? StartedAt);
}
