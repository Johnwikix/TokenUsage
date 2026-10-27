using System.Text.Json;
using TokenUsage.Config;
using TokenUsage.Providers.Common;

namespace TokenUsage.Providers.Balance;

internal sealed class BalanceSpec
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string AuthHint { get; init; }
    public required Func<ProviderConfig, IReadOnlyList<Endpoint>> Endpoints { get; init; }
    public required Func<ProviderConfig, JsonElement, BalanceReport> Parse { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

internal sealed class BalanceProvider : IProvider
{
    private readonly BalanceSpec _spec;

    public BalanceProvider(BalanceSpec spec) => _spec = spec;

    public string Name => _spec.Name;
    public string DisplayTitle => _spec.Title;

    public async Task<string> FetchAndRenderAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        var (document, _) = await HttpProbe.GetJsonAnyAsync(
            _spec.Endpoints(config),
            config.Token!,
            _spec.Headers,
            _spec.Timeout,
            _spec.AuthHint,
            cancellationToken);

        using (document)
        {
            var report = _spec.Parse(config, document.RootElement);
            return ReportRenderer.RenderBalance(_spec.Title, report);
        }
    }
}

internal static class BalanceProviders
{
    public static IReadOnlyList<IProvider> CreateAll() => new IProvider[]
    {
        DeepSeek(),
        Moonshot(),
        OpenRouter(),
        SiliconFlow(),
        ZenMux(),
    };

    private static IProvider DeepSeek() => new BalanceProvider(new BalanceSpec
    {
        Name = "deepseek",
        Title = "DeepSeek API 余额（按量计费）",
        AuthHint = "token 无效或已过期，请登录 https://platform.deepseek.com 重置 API Key",
        Endpoints = cfg => new[] { new Endpoint(Url(cfg, "https://api.deepseek.com/user/balance", "/user/balance")) },
        Parse = (_, root) => ParseDeepSeek(root),
    });

    private static IProvider Moonshot() => new BalanceProvider(new BalanceSpec
    {
        Name = "moonshot",
        Title = "Moonshot / Kimi 开放平台余额（按量计费）",
        AuthHint = "token 无效或已过期，请登录 https://platform.moonshot.cn 重置 API Key",
        Endpoints = cfg => cfg.IsGlobalRegion
            ? new[] { new Endpoint(Url(cfg, "https://api.moonshot.ai/v1/users/me/balance", "/v1/users/me/balance")) }
            : new[] { new Endpoint(Url(cfg, "https://api.moonshot.cn/v1/users/me/balance", "/v1/users/me/balance")) },
        Parse = (cfg, root) => ParseMoonshot(root, cfg),
    });

    private static IProvider OpenRouter() => new BalanceProvider(new BalanceSpec
    {
        Name = "openrouter",
        Title = "OpenRouter 余额（按量计费）",
        AuthHint = "token 无效或已过期，请登录 https://openrouter.ai/keys 重置 API Key",
        Endpoints = cfg => new[] { new Endpoint(Url(cfg, "https://openrouter.ai/api/v1/credits", "/api/v1/credits")) },
        Parse = (_, root) => ParseOpenRouter(root),
    });

    private static IProvider SiliconFlow() => new BalanceProvider(new BalanceSpec
    {
        Name = "siliconflow",
        Title = "SiliconFlow 余额（按量计费）",
        AuthHint = "token 无效或已过期，请登录 https://cloud.siliconflow.cn 重置 API Key",
        Endpoints = cfg => cfg.IsGlobalRegion
            ? new[] { new Endpoint(Url(cfg, "https://api.siliconflow.com/v1/user/info", "/v1/user/info")) }
            : new[] { new Endpoint(Url(cfg, "https://api.siliconflow.cn/v1/user/info", "/v1/user/info")) },
        Parse = (cfg, root) => ParseSiliconFlow(root, cfg),
    });

    private static IProvider ZenMux() => new BalanceProvider(new BalanceSpec
    {
        Name = "zenmux",
        Title = "ZenMux 余额（按量计费）",
        AuthHint = "token 无效或已过期，请登录 https://zenmux.ai 重置 API Key",
        Endpoints = cfg => new[] { new Endpoint(Url(cfg, "https://zenmux.ai/api/v1/management/payg/balance", "/api/v1/management/payg/balance")) },
        Parse = (_, root) => ParseZenMux(root),
    });

    private static string Url(ProviderConfig config, string defaultUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(config.BaseUrl)) return defaultUrl;
        var baseUrl = config.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.EndsWith(path, StringComparison.OrdinalIgnoreCase)) return baseUrl;
        return baseUrl + path;
    }

    internal static BalanceReport ParseDeepSeek(JsonElement root)
    {
        var infos = JsonUtil.Array(root, "balance_infos");
        if (infos.Count == 0)
        {
            throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 balance_infos");
        }

        var info = infos[0];
        var currency = JsonUtil.Str(info, "currency") ?? "CNY";
        var total = JsonUtil.Str(info, "total_balance") ?? "0";
        var lines = new List<BalanceLine>();
        AddMoney(lines, "充值余额", currency, JsonUtil.Str(info, "topped_up_balance"));
        AddMoney(lines, "未过期赠金", currency, JsonUtil.Str(info, "granted_balance"));

        var available = JsonUtil.Bool(root, "is_available");
        return new BalanceReport
        {
            Currency = currency,
            Available = total,
            Lines = lines,
            Note = available == false ? "账户当前不可用（is_available=false）" : null,
        };
    }

    internal static BalanceReport ParseMoonshot(JsonElement root, ProviderConfig config)
    {
        var data = JsonUtil.Prop(root, "data")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 data");
        var currency = config.IsGlobalRegion ? "USD" : "CNY";
        var available = JsonUtil.Num(data, "available_balance")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 available_balance");

        var lines = new List<BalanceLine>();
        AddMoney(lines, "现金余额", currency, JsonUtil.Num(data, "cash_balance"));
        AddMoney(lines, "代金券余额", currency, JsonUtil.Num(data, "voucher_balance"));

        return new BalanceReport
        {
            Currency = currency,
            Available = ReportRenderer.Money(available),
            Lines = lines,
        };
    }

    internal static BalanceReport ParseOpenRouter(JsonElement root)
    {
        var data = JsonUtil.Prop(root, "data")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 data");
        var credits = JsonUtil.Num(data, "total_credits")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 total_credits");
        var usage = JsonUtil.Num(data, "total_usage") ?? 0;

        return new BalanceReport
        {
            Currency = "USD",
            Available = ReportRenderer.Money(credits - usage),
            Lines = new List<BalanceLine>
            {
                new() { Label = "已购买额度", Value = ReportRenderer.FormatMoney("USD", ReportRenderer.Money(credits)) },
                new() { Label = "已使用", Value = ReportRenderer.FormatMoney("USD", ReportRenderer.Money(usage)) },
            },
        };
    }

    internal static BalanceReport ParseSiliconFlow(JsonElement root, ProviderConfig config)
    {
        var data = JsonUtil.Prop(root, "data")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 data");
        var total = JsonUtil.FirstStr(data, "totalBalance", "total_balance")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 totalBalance");
        var currency = config.IsGlobalRegion ? "USD" : "CNY";

        var lines = new List<BalanceLine>();
        AddMoney(lines, "充值余额", currency, JsonUtil.FirstNum(data, "chargeBalance", "charge_balance"));
        AddMoney(lines, "赠费余额", currency, JsonUtil.FirstNum(data, "balance"));

        return new BalanceReport
        {
            Currency = currency,
            Available = total,
            Lines = lines,
        };
    }

    internal static BalanceReport ParseZenMux(JsonElement root)
    {
        var data = JsonUtil.Prop(root, "data")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 data");
        var credits = JsonUtil.FirstNum(data, "total_credits", "totalCredits")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 total_credits");

        return new BalanceReport
        {
            Currency = "USD",
            Available = ReportRenderer.Money(credits),
        };
    }

    private static void AddMoney(List<BalanceLine> lines, string label, string currency, string? amount)
    {
        if (string.IsNullOrWhiteSpace(amount)) return;
        lines.Add(new BalanceLine
        {
            Label = label,
            Value = ReportRenderer.FormatMoney(currency, amount),
        });
    }

    private static void AddMoney(List<BalanceLine> lines, string label, string currency, double? amount)
    {
        if (amount is not { } value) return;
        AddMoney(lines, label, currency, ReportRenderer.Money(value));
    }
}
