using System.Globalization;
using System.Text;

namespace TokenUsage.Providers.Minimax;

internal static class MinimaxRenderer
{
    private const int BarWidth = 20;
    private const string Filled = "█";
    private const string Empty = "░";

    public static string Render(IReadOnlyList<ModelRemain> models)
    {
        var sb = new StringBuilder();
        sb.AppendLine("MiniMax Coding Plan 用量");
        sb.AppendLine("========================");

        if (models.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("未配置任何模型额度");
        }
        else
        {
            for (int i = 0; i < models.Count; i++)
            {
                var m = models[i];
                sb.AppendLine();
                sb.AppendLine($"[{DisplayModelName(m.ModelName)}]");
                sb.Append("  5h 剩余: ");
                sb.Append(BuildLine(
                    m.CurrentIntervalRemainingPercent ?? 0,
                    ComputeRemaining(m.CurrentIntervalTotalCount, m.CurrentIntervalRemainingPercent ?? 0),
                    m.CurrentIntervalTotalCount,
                    m.RemainsTime));
                sb.AppendLine();
                sb.Append("  周剩余: ");
                sb.Append(BuildLine(
                    m.CurrentWeeklyRemainingPercent ?? 0,
                    ComputeRemaining(m.CurrentWeeklyTotalCount, m.CurrentWeeklyRemainingPercent ?? 0),
                    m.CurrentWeeklyTotalCount,
                    m.WeeklyRemainsTime));
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.Append("刷新时间: ");
        sb.Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    private static string DisplayModelName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "(未命名模型)" : name!;

    private static long ComputeRemaining(long total, double percent)
    {
        if (total <= 0 || percent <= 0) return 0;
        return (long)Math.Round(total * percent / 100.0);
    }

    private static string BuildLine(double percent, long remaining, long total, long resetMs)
    {
        var bar = BuildBar(percent);
        var countPart = total > 0 ? $"({remaining}/{total})" : string.Empty;
        var timePart = FormatReset(resetMs);
        return $"{bar} {percent:0}% {countPart} — {timePart}";
    }

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

    private static string FormatReset(long ms)
    {
        if (ms <= 0) return "已重置";
        var totalMinutes = ms / 60000;
        var days = totalMinutes / (60 * 24);
        var hours = (totalMinutes / 60) % 24;
        var minutes = totalMinutes % 60;
        if (days > 0) return $"{days} 天 {hours} 小时后重置";
        if (hours > 0) return $"{hours} 小时 {minutes} 分后重置";
        return $"{minutes} 分后重置";
    }
}