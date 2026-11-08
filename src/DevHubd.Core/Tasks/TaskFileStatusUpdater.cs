using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevHubd.Core.Tasks;

/// <summary>
/// 任务文件状态回写：审批结果需要同时落到 <c>task_view</c> 与任务文件本身，
/// 否则扫描器下一轮会按文件把状态改回去，形成"半边同步"。
/// 只改 <c>status</c> 字段，其余内容原样保留；写入使用临时文件 + 替换，避免读到半截内容。
/// </summary>
public static class TaskFileStatusUpdater
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,

        // 任务文件是人工可读的：保留中文原文，避免被转义成 \uXXXX。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 把任务文件的 <c>status</c> 改成指定值。
    /// </summary>
    /// <param name="path">任务文件绝对路径。</param>
    /// <param name="status">目标状态：<c>done</c> / <c>ready</c> 等。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示已写入；false 表示文件不存在或不是 JSON 对象。</returns>
    public static async Task<bool> TrySetStatusAsync(
        string path,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var text = await File.ReadAllTextAsync(path, _utf8NoBom, cancellationToken).ConfigureAwait(false);
        if (JsonNode.Parse(text) is not JsonObject root)
        {
            return false;
        }

        root["status"] = status;
        var json = root.ToJsonString(_writeOptions);

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            await File.WriteAllTextAsync(path, json, _utf8NoBom, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, json, _utf8NoBom, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                // 收尾清理失败不影响已完成的替换；临时文件以 .tmp 结尾，扫描器只认 *.json，不会被误物化。
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // 同上：清理是尽力而为，主结果已由 File.Move 决定。
                }
            }
        }
    }
}
