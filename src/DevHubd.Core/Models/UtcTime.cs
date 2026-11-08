using System.Globalization;

namespace DevHubd.Core.Models;

/// <summary>
/// 对外时间格式的单一实现：MCP 契约统一使用 UTC + <c>Z</c> 后缀（<c>2026-11-08T04:09:03.1234567Z</c>）。
/// <para>
/// 库内历史行可能写成 <c>+00:00</c>（<see cref="DateTimeOffset.ToString(string)"/> 的 "O" 格式），
/// 因此读侧必须经 <see cref="Normalize"/> 再出契约，写侧不要依赖格式化后的字符串做排序键。
/// </para>
/// </summary>
public static class UtcTime
{
    private const string UtcFormat = "yyyy-MM-ddTHH:mm:ss.fffffff'Z'";

    /// <summary>当前 UTC 时间戳（契约格式）。</summary>
    public static string Now() => Format(DateTimeOffset.UtcNow);

    /// <summary>把时间点格式化为契约要求的 UTC + Z 字符串。</summary>
    /// <param name="value">时间点。</param>
    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(UtcFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// 把库内时间字符串归一为 UTC + Z；入参非空时保证返回非空（无法解析则原样返回）。
    /// </summary>
    /// <param name="value">库内时间字符串。</param>
    public static string NormalizeRequired(string value) => Normalize(value) ?? string.Empty;

    /// <summary>
    /// 把库内时间字符串归一为 UTC + Z；无法解析时原样返回（不丢数据、不抛异常）。
    /// </summary>
    /// <param name="value">库内时间字符串。</param>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? Format(parsed)
            : value;
    }
}
