using System.Text.Json;
using TokenUsage.Config;
using TokenUsage.Providers.Common;

namespace TokenUsage.Providers.Minimax;

internal sealed class MinimaxProvider : IProvider
{
    public string Name => "minimax";
    public string DisplayTitle => "MiniMax Coding Plan 用量";

    private const string Path = "/v1/api/openplatform/coding_plan/remains";
    private const string AuthHint =
        "token 无效或已过期，请登录 https://platform.minimaxi.com/user-center/payment/coding-plan 重置 API Key";

    public async Task<string> FetchAndRenderAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = config.IsGlobalRegion ? "https://platform.minimax.io/" : "https://platform.minimaxi.com/",
        };

        var (document, _) = await HttpProbe.GetJsonAnyAsync(
            BuildEndpoints(config),
            config.Token!,
            headers,
            TimeSpan.FromSeconds(15),
            AuthHint,
            cancellationToken);

        using (document)
        {
            return ReportRenderer.RenderPlan(DisplayTitle, Parse(document.RootElement));
        }
    }

    private static IReadOnlyList<Endpoint> BuildEndpoints(ProviderConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            var baseUrl = config.BaseUrl.Trim().TrimEnd('/');
            return new[]
            {
                new Endpoint(baseUrl.EndsWith(Path, StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + Path),
            };
        }

        if (config.IsGlobalRegion)
        {
            return new[] { new Endpoint("https://api.minimax.io" + Path) };
        }

        // 国内历史上同时存在 www 与 api 两个主机名，两个都试。
        return new[]
        {
            new Endpoint("https://www.minimaxi.com" + Path),
            new Endpoint("https://api.minimaxi.com" + Path),
        };
    }

    /// <summary>
    /// MiniMax 返回的是“剩余百分比”，这里统一换算成已用百分比。
    /// 5h 窗口恒有；周窗口仅在 current_weekly_status == 1（或字段缺省）时展示。
    /// </summary>
    internal static PlanReport Parse(JsonElement root)
    {
        var all = JsonUtil.Array(root, "model_remains");
        // 同一响应里还带 video 等非编程额度，只取 general 这一行；没有 general 时才退回全部。
        var general = all.Where(model => JsonUtil.Str(model, "model_name") == "general").ToList();
        var models = general.Count > 0 ? general : all;
        var windows = new List<PlanWindow>();

        foreach (var model in models)
        {
            var modelName = JsonUtil.Str(model, "model_name");
            var prefix = models.Count > 1 && !string.IsNullOrWhiteSpace(modelName)
                ? $" · {modelName}"
                : string.Empty;

            var intervalRemaining = JsonUtil.Num(model, "current_interval_remaining_percent");
            if (intervalRemaining is not null)
            {
                var usedPercent = Math.Clamp(100d - intervalRemaining.Value, 0, 100);
                var total = JsonUtil.Num(model, "current_interval_total_count");
                windows.Add(new PlanWindow
                {
                    Label = "5h 滚动窗口" + prefix,
                    Percent = usedPercent,
                    Used = total is > 0 ? total.Value * usedPercent / 100d : null,
                    Limit = total is > 0 ? total : null,
                    ResetMs = ResolveResetMs(model, "end_time", "remains_time"),
                });
            }

            var weeklyStatus = JsonUtil.Long(model, "current_weekly_status");
            var weeklyRemaining = JsonUtil.Num(model, "current_weekly_remaining_percent");
            if (weeklyRemaining is not null && weeklyStatus is null or 1)
            {
                var usedPercent = Math.Clamp(100d - weeklyRemaining.Value, 0, 100);
                var total = JsonUtil.Num(model, "current_weekly_total_count");
                windows.Add(new PlanWindow
                {
                    Label = "周窗口" + prefix,
                    Percent = usedPercent,
                    Used = total is > 0 ? total.Value * usedPercent / 100d : null,
                    Limit = total is > 0 ? total : null,
                    ResetMs = ResolveResetMs(model, "weekly_end_time", "weekly_remains_time"),
                });
            }
        }

        return new PlanReport
        {
            Windows = windows,
            Note = windows.Count == 0 ? "未返回 model_remains 额度" : null,
        };
    }

    private static long ResolveResetMs(JsonElement model, string endTimeKey, string remainsTimeKey)
    {
        var absolute = TimeUtil.ParseInstantMs(JsonUtil.Prop(model, endTimeKey));
        if (absolute > 0) return absolute;

        var remaining = JsonUtil.ToLong(JsonUtil.Prop(model, remainsTimeKey) ?? default);
        if (remaining is > 0)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + remaining.Value;
        }
        return 0;
    }
}
