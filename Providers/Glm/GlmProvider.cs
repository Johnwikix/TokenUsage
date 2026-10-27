using System.Text.Json;
using TokenUsage.Config;
using TokenUsage.Providers.Common;

namespace TokenUsage.Providers.Glm;

internal sealed class GlmProvider : IProvider
{
    public string Name => "glm";
    public string DisplayTitle => "GLM Coding Plan 用量";

    private const string AuthHint =
        "token 无效或已过期，请登录 https://open.bigmodel.cn 或 https://z.ai 重置 API Key";

    private const string Path = "/api/monitor/usage/quota/limit";

    public async Task<string> FetchAndRenderAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        // 智谱两种鉴权方式都被不同客户端使用过：
        //   - z.ai 官方 / opencodex: Authorization: Bearer <key>
        //   - dsh-glm-usage / pi-glm-quota: 直接用 key 作值（无 Bearer 前缀）
        // 逐个组合尝试，第一个 2xx 的响应生效。
        var endpoints = BuildEndpoints(config);
        var headers = new Dictionary<string, string>
        {
            ["accept-language"] = "en-US,en",
        };

        var (document, _) = await HttpProbe.GetJsonAnyAsync(
            endpoints,
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
        var hosts = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            hosts.Add(config.BaseUrl.Trim().TrimEnd('/'));
        }
        else if (config.IsGlobalRegion)
        {
            hosts.Add("https://api.z.ai");
        }
        else
        {
            hosts.Add("https://open.bigmodel.cn");
            hosts.Add("https://api.z.ai");
        }

        var endpoints = new List<Endpoint>(hosts.Count * 2);
        foreach (var host in hosts)
        {
            var url = host.EndsWith(Path, StringComparison.OrdinalIgnoreCase) ? host : host + Path;
            endpoints.Add(new Endpoint(url, Bearer: false));
            endpoints.Add(new Endpoint(url, Bearer: true));
        }
        return endpoints;
    }

    /// <summary>
    /// 窗口按智谱自己的 unit 码分类，避免新增/重排行导致窗口串位：
    ///   3 = 5 小时滚动窗口，6 = 每周窗口，5 = 每月窗口。
    /// TOKENS_LIMIT 走 percentage；CREDIT_LIMIT 优先用 currentValue / usage 的精确比例。
    /// TIME_LIMIT 是 MCP 请求上限，不是套餐窗口，单独标记为明细行。
    /// </summary>
    internal static PlanReport Parse(JsonElement root)
    {
        if (JsonUtil.Bool(root, "success") is false)
        {
            throw new ProviderException(
                ProviderExceptionKind.ApiError,
                JsonUtil.Str(root, "msg") ?? "智谱接口返回 success=false");
        }

        var data = JsonUtil.Prop(root, "data")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 data");

        var windows = new List<PlanWindow>();

        foreach (var row in JsonUtil.Array(data, "limits"))
        {
            var type = (JsonUtil.Str(row, "type") ?? string.Empty).ToUpperInvariant();
            var resetMs = TimeUtil.ParseInstantMs(JsonUtil.FirstProp(row, "nextResetTime", "next_reset_time"));

            if (type == "TIME_LIMIT")
            {
                var percent = PercentFromRatio(row) ?? ClampPercent(JsonUtil.Num(row, "percentage"));
                if (percent is null) continue;
                windows.Add(new PlanWindow
                {
                    Label = "MCP 月请求（Web Search / Web Reader / Zread）",
                    Percent = percent,
                    ResetMs = resetMs,
                    IsDetail = true,
                });
                continue;
            }

            if (type != "TOKENS_LIMIT" && type != "CREDIT_LIMIT") continue;

            var label = (JsonUtil.Long(row, "unit")) switch
            {
                3 => "5h 滚动窗口",
                6 => "周窗口",
                5 => "月窗口",
                _ => null,
            };
            if (label is null) continue;

            var rowPercent = type == "CREDIT_LIMIT"
                ? PercentFromRatio(row) ?? ClampPercent(JsonUtil.Num(row, "percentage"))
                : ClampPercent(JsonUtil.Num(row, "percentage")) ?? PercentFromRatio(row);
            if (rowPercent is null) continue;

            windows.Add(new PlanWindow
            {
                Label = label,
                Percent = rowPercent,
                Used = JsonUtil.Num(row, "currentValue"),
                Limit = JsonUtil.Num(row, "usage"),
                ResetMs = resetMs,
            });
        }

        return new PlanReport
        {
            PlanName = JsonUtil.Str(data, "level"),
            Windows = windows,
            Note = windows.Count == 0
                ? "未返回可识别的套餐窗口（TOKENS_LIMIT / CREDIT_LIMIT 的 unit 3/6/5）"
                : null,
        };
    }

    private static double? PercentFromRatio(JsonElement row)
    {
        var used = JsonUtil.Num(row, "currentValue");
        var limit = JsonUtil.Num(row, "usage");
        if (used is null || limit is null || limit <= 0) return null;
        return Math.Clamp(used.Value / limit.Value * 100d, 0, 100);
    }

    private static double? ClampPercent(double? value) =>
        value is null ? null : Math.Clamp(value.Value, 0, 100);
}
