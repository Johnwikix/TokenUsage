using System.Globalization;
using System.Text.Json;

namespace TokenUsage.Providers.Common;

internal static class TimeUtil
{
    /// <summary>
    /// 把服务端时间归一化成 unix epoch 毫秒。
    /// 兼容 epoch 秒 / epoch 毫秒（数字或数字字符串）与 ISO 8601。
    /// </summary>
    public static long ParseInstantMs(JsonElement? value)
    {
        if (value is not { } element) return 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out var number) ? FromNumber(number) : 0,
            JsonValueKind.String => ParseInstantMs(element.GetString()),
            _ => 0,
        };
    }

    public static long ParseInstantMs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        text = text.Trim();

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return FromNumber(integer);
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return FromNumber(number);
        }
        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var instant))
        {
            return instant.ToUnixTimeMilliseconds();
        }
        return 0;
    }

    public static long FromNumber(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return 0;
        var ms = value < 1_000_000_000_000d
            ? Math.Round(value * 1000d)
            : Math.Round(value);
        return ms is > 8_640_000_000_000_000d or < -8_640_000_000_000_000d ? 0 : (long)ms;
    }

    public static string FormatReset(long epochMs)
    {
        if (epochMs <= 0) return "无重置时间";
        var diffMs = epochMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (diffMs <= 0) return "即将重置";

        var totalMinutes = diffMs / 60_000;
        var days = totalMinutes / (60 * 24);
        var hours = totalMinutes / 60 % 24;
        var minutes = totalMinutes % 60;
        if (days > 0) return $"{days} 天 {hours} 小时后重置";
        if (hours > 0) return $"{hours} 小时 {minutes} 分后重置";
        if (minutes > 0) return $"{minutes} 分后重置";
        return "<1 分后重置";
    }

    public static string FormatInstant(long epochMs)
    {
        if (epochMs <= 0) return "无重置时间";
        return DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
            .ToLocalTime()
            .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
