using System.Globalization;
using System.Text;

namespace TokenUsage.Providers.Glm;

internal static class GlmRenderer
{
    private const int BarWidth = 20;
    private const string Filled = "█";
    private const string Empty = "░";

    public static string Render(GlmUsage usage)
    {
        return RenderInternal(usage);
    }

    /// <summary>Self-test 入口；行为与 Render 一致，仅暴露给 GlmUsageTests。</summary>
    internal static string RenderForTest(GlmUsage usage) => RenderInternal(usage);

    private static string RenderInternal(GlmUsage usage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("GLM Token Plan 用量");
        sb.AppendLine("==================");

        if (!string.IsNullOrWhiteSpace(usage.Level))
        {
            sb.Append("等级: ").Append(usage.Level);
            if (!string.IsNullOrWhiteSpace(usage.Host))
            {
                sb.Append("    (host: ").Append(usage.Host).Append(')');
            }
            sb.AppendLine();
        }

        if (usage.FiveHour is null && usage.Weekly is null && usage.Monthly is null)
        {
            sb.AppendLine();
            sb.AppendLine("未配置任何窗口额度（限额数据为空或 type 不是 TOKENS_LIMIT / CREDIT_LIMIT / TIME_LIMIT）");
        }
        else
        {
            if (usage.FiveHour is not null)
            {
                sb.AppendLine();
                sb.AppendLine(BuildSection("[5h 滚动窗口]", usage.FiveHour));
            }
            if (usage.Weekly is not null)
            {
                sb.AppendLine();
                sb.AppendLine(BuildSection("[周窗口]", usage.Weekly));
            }
            if (usage.Monthly is not null)
            {
                sb.AppendLine();
                sb.AppendLine(BuildSection("[月 MCP（Web Search / Web Reader / Zread）]", usage.Monthly));
            }
        }

        sb.AppendLine();
        sb.Append("刷新时间: ");
        sb.Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    private static string BuildSection(string title, QuotaWindow w)
    {
        var bar = BuildBar(w.Percent);
        var pct = w.Percent.ToString("0", CultureInfo.InvariantCulture);
        var countPart = w.Total > 0 ? $"({FormatNum(w.Used)}/{FormatNum(w.Total)})" : string.Empty;
        var timePart = FormatReset(w.ResetMs);
        return $"{title}\n  {bar} {pct}% {countPart} — {timePart}";
    }

    private static string FormatNum(double v) =>
        ((long)Math.Round(v, MidpointRounding.AwayFromZero)).ToString("N0", CultureInfo.InvariantCulture);

    private static string BuildBar(double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var filled = (int)Math.Round(clamped / 100.0 * BarWidth);
        if (filled < 0) filled = 0;
        if (filled > BarWidth) filled = BarWidth;
        var empty = BarWidth - filled;
        var sb = new StringBuilder(BarWidth * 3);
        for (int i = 0; i < filled; i++) sb.Append(Filled);
        for (int i = 0; i < empty; i++) sb.Append(Empty);
        return sb.ToString();
    }

    private static string FormatReset(long epochMs)
    {
        // epochMs 是绝对 unix 毫秒（参考 opencodex epochMillis）。
        // 必须算与"现在"的差，而不是把数字本身当相对毫秒数。
        if (epochMs <= 0) return "无重置时间";
        var diffMs = epochMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (diffMs <= 0) return "即将重置";

        var totalMinutes = diffMs / 60000;
        var days = totalMinutes / (60 * 24);
        var hours = (totalMinutes / 60) % 24;
        var minutes = totalMinutes % 60;
        if (days > 0) return $"{days} 天 {hours} 小时后重置";
        if (hours > 0) return $"{hours} 小时 {minutes} 分后重置";
        if (minutes > 0) return $"{minutes} 分后重置";
        return "<1 分后重置";
    }
}
