using System.Text.Json;
using TokenUsage.Config;
using TokenUsage.Providers.Common;

namespace TokenUsage.Providers.Kimi;

internal sealed class KimiProvider : IProvider
{
    public string Name => "kimi";
    public string DisplayTitle => "Kimi For Coding 套餐用量";

    private const string AuthHint =
        "Kimi Code API Key 无效或已过期（请使用 https://www.kimi.com/code/console 签发的 sk-kimi- Key，而不是 Moonshot 开放平台 Key）";

    public async Task<string> FetchAndRenderAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        var url = BuildUrl(config);
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/json",
        };

        var (document, _) = await HttpProbe.GetJsonAnyAsync(
            new[] { new Endpoint(url) },
            config.Token!,
            headers,
            TimeSpan.FromSeconds(15),
            AuthHint,
            cancellationToken);

        using (document)
        {
            var report = Parse(document.RootElement);
            return ReportRenderer.RenderPlan(DisplayTitle, report);
        }
    }

    internal static string BuildUrl(ProviderConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            var baseUrl = config.BaseUrl.Trim().TrimEnd('/');
            if (baseUrl.EndsWith("/usages", StringComparison.OrdinalIgnoreCase)) return baseUrl;
            if (baseUrl.EndsWith("/coding/v1", StringComparison.OrdinalIgnoreCase)) return baseUrl + "/usages";
            if (baseUrl.EndsWith("/coding", StringComparison.OrdinalIgnoreCase)) return baseUrl + "/v1/usages";
            return baseUrl + "/coding/v1/usages";
        }

        var host = config.IsGlobalRegion ? "https://api.kimi.ai" : "https://api.kimi.com";
        return host + "/coding/v1/usages";
    }

    /// <summary>
    /// Kimi Code 的 usages 响应同时提供三类额度池（新版）：
    ///   usages.limit_5h / limit_7d / limit_month_total（会员月度总额度）/ limit_month_code（Code 占比）
    /// 旧版只有 limits[]（按窗口计数）与顶层 usage（每周计数）。
    /// dsh-web 只读取了 limits[] 与顶层 usage，因此新套餐只显示 5h；这里补齐月度总额度。
    /// </summary>
    internal static PlanReport Parse(JsonElement root)
    {
        var windows = new List<PlanWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var pools = JsonUtil.Prop(root, "usages");
        var hasPools = pools is { ValueKind: JsonValueKind.Object };

        void AddPool(string key, string label, bool detail = false)
        {
            if (!hasPools) return;
            if (JsonUtil.Prop(pools!.Value, key) is not { ValueKind: JsonValueKind.Object } pool) return;
            var ratio = JsonUtil.FirstNum(pool, "used_ratio", "usedRatio");
            if (ratio is null) return;

            windows.Add(new PlanWindow
            {
                Label = label,
                Percent = Math.Clamp(ratio.Value * 100d, 0, 100),
                ResetMs = TimeUtil.ParseInstantMs(JsonUtil.FirstProp(pool, "reset_time", "resetTime")),
                IsDetail = detail,
            });
            seen.Add(key);
        }

        AddPool("limit_5h", "5h 滚动窗口");
        AddPool("limit_7d", "周窗口");
        AddPool("limit_month_total", "月度总额度");
        if (seen.Contains("limit_month_total"))
        {
            AddPool("limit_month_code", "其中 Kimi Code", detail: true);
        }

        // 旧版 limits[]：window.duration + timeUnit 决定窗口，detail 里是计数。
        foreach (var entry in JsonUtil.Array(root, "limits"))
        {
            if (JsonUtil.Prop(entry, "window") is not { ValueKind: JsonValueKind.Object } window) continue;
            if (JsonUtil.Prop(entry, "detail") is not { ValueKind: JsonValueKind.Object } detail) continue;

            var minutes = WindowMinutes(window);
            if (minutes is null) continue;

            var key = minutes switch
            {
                300 => "limit_5h",
                10_080 => "limit_7d",
                _ => null,
            };
            if (key is not null && seen.Contains(key)) continue;

            var limit = JsonUtil.Num(detail, "limit");
            var used = JsonUtil.Num(detail, "used");
            double? percent = limit is > 0 && used is not null
                ? Math.Clamp(used.Value / limit.Value * 100d, 0, 100)
                : null;

            windows.Add(new PlanWindow
            {
                Label = minutes switch
                {
                    300 => "5h 滚动窗口",
                    10_080 => "周窗口",
                    _ => FormatMinutes(minutes.Value),
                },
                Percent = percent,
                Used = used,
                Limit = limit,
                ResetMs = TimeUtil.ParseInstantMs(JsonUtil.FirstProp(detail, "resetTime", "reset_time", "reset_at")),
            });
            if (key is not null) seen.Add(key);
        }

        // 旧版顶层 usage 是每周计数摘要；新版有 limit_7d 时不再重复展示。
        if (!seen.Contains("limit_7d")
            && JsonUtil.Prop(root, "usage") is { ValueKind: JsonValueKind.Object } usage)
        {
            var limit = JsonUtil.Num(usage, "limit");
            var used = JsonUtil.Num(usage, "used");
            if (limit is > 0 || used is not null)
            {
                windows.Add(new PlanWindow
                {
                    Label = "周窗口（旧版计数）",
                    Percent = limit is > 0 && used is not null
                        ? Math.Clamp(used.Value / limit.Value * 100d, 0, 100)
                        : null,
                    Used = used,
                    Limit = limit,
                    ResetMs = TimeUtil.ParseInstantMs(JsonUtil.FirstProp(usage, "resetTime", "reset_time")),
                });
            }
        }

        var hasMonthCode = windows.Any(window => window.IsDetail);
        return new PlanReport
        {
            PlanName = ParsePlanName(root),
            Windows = windows,
            Note = hasMonthCode
                ? "月度总额度与 Kimi 会员共享；其中 Kimi Code 是同一额度池内 Code 的已用占比，不是独立额度。"
                : null,
        };
    }

    private static int? WindowMinutes(JsonElement window)
    {
        var duration = JsonUtil.Num(window, "duration");
        var unit = JsonUtil.Str(window, "timeUnit");
        if (duration is null || unit is null) return null;

        var multiplier = unit switch
        {
            "TIME_UNIT_MINUTE" => 1,
            "TIME_UNIT_HOUR" => 60,
            "TIME_UNIT_DAY" => 24 * 60,
            _ => 0,
        };
        if (multiplier == 0) return null;
        return (int)Math.Round(duration.Value * multiplier);
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes >= 24 * 60 && minutes % (24 * 60) == 0) return $"{minutes / (24 * 60)} 天窗口";
        if (minutes >= 60 && minutes % 60 == 0) return $"{minutes / 60} 小时窗口";
        return $"{minutes} 分钟窗口";
    }

    private static string? ParsePlanName(JsonElement root)
    {
        var user = JsonUtil.Prop(root, "user");
        var membership = user is { ValueKind: JsonValueKind.Object } ? JsonUtil.Prop(user.Value, "membership") : null;
        var level = membership is { ValueKind: JsonValueKind.Object } ? JsonUtil.Str(membership.Value, "level") : null;
        if (string.IsNullOrWhiteSpace(level) || level == "LEVEL_UNSPECIFIED") return null;

        return level switch
        {
            "LEVEL_FREE" => "Adagio",
            "LEVEL_TRIAL" => "Andante",
            "LEVEL_BASIC" => "Moderato",
            "LEVEL_INTERMEDIATE" => "Allegretto",
            "LEVEL_ADVANCED" => "Allegro",
            _ => level,
        };
    }
}
