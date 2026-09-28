namespace TokenUsage.Providers.Glm;

/// <summary>
/// 归一化后的智谱配额快照。
/// 来自 opencodex quotas.ts parseZaiQuotaLimits + pi-glm-quota classifyLimits 综合规则：
///   - TOKENS_LIMIT / CREDIT_LIMIT 行的 unit/number 共同决定窗口
///     · unit=3  number=5 → 5h 滚动窗口
///     · unit=6  number=1 → 周窗口
///   - TIME_LIMIT 行 → 月度 MCP（Web Search / Web Reader / Zread）
///   - 缺 percentage 时回退 currentValue / usage
///   - nextResetTime 是 unix epoch 毫秒（参考 opencodex quota-wire.ts epochMillis）：
///     · 值 > 10_000_000_000 直接当 ms
///     · 否则当 s，再 * 1000；<=0 当作无重置时间
/// </summary>
internal sealed class GlmUsage
{
    public string? Level { get; init; }
    public string Host { get; init; } = string.Empty;
    public QuotaWindow? FiveHour { get; init; }
    public QuotaWindow? Weekly { get; init; }
    public QuotaWindow? Monthly { get; init; }
    public string? Error { get; init; }

    public static GlmUsage Parse(QuotaData data, string host)
    {
        QuotaWindow? fiveHour = null;
        QuotaWindow? weekly = null;
        QuotaWindow? monthly = null;

        foreach (var raw in data.Limits)
        {
            if (raw is null) continue;

            var resetMs = EpochMillis(raw.NextResetTime);

            // percentage 缺位时回退 currentValue / usage（实测的 5h/7d 行偶尔不带 percentage）
            double? percent = raw.Percentage;
            if (percent is null && raw.CurrentValue is { } used && raw.Usage is { } total && total > 0)
            {
                percent = used / total * 100.0;
            }
            if (percent is null) continue;

            var clamped = Math.Clamp(percent.Value, 0, 100);

            var type = raw.Type?.ToUpperInvariant();

            if (type == "TIME_LIMIT")
            {
                monthly = new QuotaWindow
                {
                    Label = "月 MCP",
                    Percent = clamped,
                    Used = raw.CurrentValue ?? 0,
                    Total = raw.Usage ?? 0,
                    Remaining = raw.Remaining ?? 0,
                    ResetMs = resetMs,
                };
                continue;
            }

            if (type != "TOKENS_LIMIT" && type != "CREDIT_LIMIT") continue;

            var unit = raw.Unit ?? 0;
            var number = raw.Number ?? 0;

            if (unit == 3 && number == 5)
            {
                fiveHour = new QuotaWindow
                {
                    Label = "5h",
                    Percent = clamped,
                    Used = raw.CurrentValue ?? 0,
                    Total = raw.Usage ?? 0,
                    Remaining = raw.Remaining ?? 0,
                    ResetMs = resetMs,
                };
            }
            else if (unit == 6 && number == 1)
            {
                weekly = new QuotaWindow
                {
                    Label = "周",
                    Percent = clamped,
                    Used = raw.CurrentValue ?? 0,
                    Total = raw.Usage ?? 0,
                    Remaining = raw.Remaining ?? 0,
                    ResetMs = resetMs,
                };
            }
        }

        return new GlmUsage
        {
            Level = data.Level,
            Host = host,
            FiveHour = fiveHour,
            Weekly = weekly,
            Monthly = monthly,
        };
    }

    /// <summary>
    /// 把服务端给的数字归一化成 unix epoch 毫秒。
    /// 复刻 opencodex quota-wire.ts epochMillis：> 10_000_000_000 视为 ms，否则视为 s 再 ×1000；
    /// 非有限 / 非正数视为无重置时间。
    /// </summary>
    internal static long EpochMillis(long? value)
    {
        if (value is null || value <= 0) return 0;
        var v = value.Value;
        var ms = v > 10_000_000_000L ? v : v * 1000L;
        // ECMAScript 时间值上限约 ±8.64e15；超出视为脏数据
        if (ms > 8_640_000_000_000_000L || ms < -8_640_000_000_000_000L) return 0;
        return ms;
    }
}

internal sealed class QuotaWindow
{
    public string Label { get; init; } = string.Empty;
    public double Percent { get; init; }
    public double Used { get; init; }
    public double Total { get; init; }
    public double Remaining { get; init; }
    public long ResetMs { get; init; }
}