using System.Globalization;
using System.Text;

namespace TokenUsage.Providers.Common;

internal static class ReportRenderer
{
    private const int BarWidth = 20;
    private const string Filled = "█";
    private const string Empty = "░";

    public static string RenderBalance(string title, BalanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine(new string('=', Math.Max(4, DisplayWidth(title))));
        sb.AppendLine();
        sb.Append("可用余额: ");
        sb.AppendLine(FormatMoney(report.Currency, report.Available));

        foreach (var line in report.Lines)
        {
            sb.Append("  ").Append(line.Label).Append(": ").AppendLine(line.Value);
        }

        if (!string.IsNullOrWhiteSpace(report.Note))
        {
            sb.AppendLine();
            sb.AppendLine(report.Note);
        }

        sb.AppendLine();
        sb.Append("刷新时间: ");
        sb.Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static string RenderPlan(string title, PlanReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine(new string('=', Math.Max(4, DisplayWidth(title))));

        if (!string.IsNullOrWhiteSpace(report.PlanName))
        {
            sb.Append("套餐: ").AppendLine(report.PlanName);
        }

        if (report.Windows.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("未返回任何额度窗口");
        }
        else
        {
            foreach (var window in report.Windows)
            {
                sb.AppendLine();
                if (window.IsDetail)
                {
                    sb.Append("  └ ").Append(window.Label).Append(": ");
                    sb.Append(window.Percent is { } detailPercent
                        ? $"已用 {detailPercent:0.#}%"
                        : "未知");
                    sb.Append(" — ").AppendLine(TimeUtil.FormatReset(window.ResetMs));
                    if (!string.IsNullOrWhiteSpace(window.Note))
                    {
                        sb.Append("    ").AppendLine(window.Note);
                    }
                    continue;
                }

                sb.Append('[').Append(window.Label).AppendLine("]");
                sb.Append("  ");
                if (window.Percent is { } percent)
                {
                    // 与官网控制台一致：以「剩余」为主，条形图按剩余填充（满格 = 额度充足）。
                    var remaining = Math.Clamp(100 - percent, 0, 100);
                    sb.Append(BuildBar(remaining)).Append(' ');
                    sb.Append("剩余 ").Append(FormatPercent(remaining)).Append('%');
                    sb.Append(" · 已用 ").Append(FormatPercent(percent)).Append('%');
                }
                else
                {
                    sb.Append("用量未知");
                }

                if (window.Used is { } used && window.Limit is { } limit && limit > 0)
                {
                    sb.Append(" (").Append(FormatNum(used)).Append('/').Append(FormatNum(limit)).Append(')');
                }

                sb.Append(" — ").Append(TimeUtil.FormatReset(window.ResetMs));
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(window.Note))
                {
                    sb.Append("  ").AppendLine(window.Note);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(report.Note))
        {
            sb.AppendLine();
            sb.AppendLine(report.Note);
        }

        sb.AppendLine();
        sb.Append("刷新时间: ");
        sb.Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static string BuildBar(double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var filled = (int)Math.Round(clamped / 100.0 * BarWidth);
        filled = Math.Clamp(filled, 0, BarWidth);
        var sb = new StringBuilder(BarWidth * 3);
        for (int i = 0; i < filled; i++) sb.Append(Filled);
        for (int i = 0; i < BarWidth - filled; i++) sb.Append(Empty);
        return sb.ToString();
    }

    private static string FormatPercent(double value) =>
        Math.Clamp(value, 0, 100).ToString("0.#", CultureInfo.InvariantCulture);

    public static string FormatNum(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return Math.Abs(value - rounded) < 0.05
            ? rounded.ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("N1", CultureInfo.InvariantCulture);
    }

    public static string FormatMoney(string currency, string amount)
    {
        var text = string.IsNullOrWhiteSpace(amount) ? "0" : amount;
        var upper = (currency ?? string.Empty).ToUpperInvariant();
        return upper switch
        {
            "CNY" => "¥" + text,
            "USD" => "$" + text,
            "" => text,
            _ => text + " " + upper,
        };
    }

    public static string Money(double value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var ch in text)
        {
            width += ch > 0x7F ? 2 : 1;
        }
        return width;
    }
}
