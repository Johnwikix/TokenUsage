using System.Text;
using System.Text.Json;
using TokenUsage.Config;
using TokenUsage.Providers.Balance;
using TokenUsage.Providers.Codex;
using TokenUsage.Providers.Common;
using TokenUsage.Providers.Glm;
using TokenUsage.Providers.Kimi;
using TokenUsage.Providers.Minimax;
using TokenUsage.Providers.OpenCode;

namespace TokenUsage.Providers;

/// <summary>
/// 解析逻辑的离线自检：直接给各 provider 的 Parse 喂入真实响应样本，
/// 验证窗口分类、百分比换算、重置时间归一化。真实 API 路径由实跑覆盖。
/// </summary>
internal static class SelfTests
{
    public static int RunAll()
    {
        var fails = 0;
        Console.WriteLine("TokenUsage 自检");

        TestKimiNewPools(ref fails);
        TestKimiLegacy(ref fails);
        TestKimiUrl(ref fails);
        TestGlm(ref fails);
        TestMinimax(ref fails);
        TestOpenCode(ref fails);
        TestCodex(ref fails);
        TestCodexAuth(ref fails);
        TestBalanceParsers(ref fails);
        TestTimeUtil(ref fails);
        TestRenderer(ref fails);

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "全部通过" : $"失败 {fails} 项");
        return fails;
    }

    private const string KimiNewJson = """
    {
      "usages": {
        "limit_5h":        { "used_ratio": 0.42, "reset_time": 1789000000000 },
        "limit_7d":        { "used_ratio": 0.73, "reset_time": 1789600000000 },
        "limit_month_total": { "used_ratio": 0.61, "reset_time": 1790200000000 },
        "limit_month_code":  { "used_ratio": 0.18, "reset_time": 1790200000000 }
      },
      "user": { "membership": { "level": "LEVEL_ADVANCED" } }
    }
    """;

    private static void TestKimiNewPools(ref int fails)
    {
        var report = KimiProvider.Parse(ParseJson(KimiNewJson));

        Assert(ref fails, "kimi.new.planName", report.PlanName, "Allegro");
        Assert(ref fails, "kimi.new.windowCount", report.Windows.Count, 4);
        Assert(ref fails, "kimi.new.has5h", Find(report, "5h 滚动窗口")?.Percent, 42.0);
        Assert(ref fails, "kimi.new.hasWeek", Find(report, "周窗口")?.Percent, 73.0);
        // 用户反馈的核心问题：dsh-web 只有 5h，没有总用量
        Assert(ref fails, "kimi.new.hasMonthTotal", Find(report, "月度总额度")?.Percent, 61.0);
        Assert(ref fails, "kimi.new.hasMonthCode", Find(report, "其中 Kimi Code")?.Percent, 18.0);
        Assert(ref fails, "kimi.new.monthCodeIsDetail", Find(report, "其中 Kimi Code")?.IsDetail, true);
        Assert(ref fails, "kimi.new.resetMs", Find(report, "5h 滚动窗口")?.ResetMs, 1789000000000L);
        AssertContains(ref fails, "kimi.new.note", report.Note ?? string.Empty, "共享");
    }

    private const string KimiLegacyJson = """
    {
      "limits": [
        {
          "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
          "detail": { "limit": 1000, "used": 250, "resetTime": "2026-09-09T12:00:00Z" }
        }
      ],
      "usage": { "limit": 5000, "used": 1500, "resetTime": "2026-09-15T00:00:00Z" },
      "user": { "membership": { "level": "LEVEL_INTERMEDIATE" } }
    }
    """;

    private static void TestKimiLegacy(ref int fails)
    {
        var report = KimiProvider.Parse(ParseJson(KimiLegacyJson));

        Assert(ref fails, "kimi.legacy.planName", report.PlanName, "Allegretto");
        Assert(ref fails, "kimi.legacy.5h.percent", Find(report, "5h 滚动窗口")?.Percent, 25.0);
        Assert(ref fails, "kimi.legacy.5h.used", Find(report, "5h 滚动窗口")?.Used, 250.0);
        Assert(ref fails, "kimi.legacy.5h.limit", Find(report, "5h 滚动窗口")?.Limit, 1000.0);
        Assert(ref fails, "kimi.legacy.week.percent", Find(report, "周窗口（旧版计数）")?.Percent, 30.0);
    }

    private static void TestKimiUrl(ref int fails)
    {
        Assert(ref fails, "kimi.url.cn", KimiProvider.BuildUrl(new ProviderConfig { Token = "x" }),
            "https://api.kimi.com/coding/v1/usages");
        Assert(ref fails, "kimi.url.global", KimiProvider.BuildUrl(new ProviderConfig { Token = "x", Region = "global" }),
            "https://api.kimi.ai/coding/v1/usages");
        Assert(ref fails, "kimi.url.baseUrl", KimiProvider.BuildUrl(new ProviderConfig { Token = "x", BaseUrl = "https://proxy.local/coding/v1" }),
            "https://proxy.local/coding/v1/usages");
    }

    private const string GlmV2Json = """
    {
      "success": true,
      "data": {
        "level": "max",
        "limits": [
          { "type": "TIME_LIMIT", "unit": 5, "number": 1, "currentValue": 40, "usage": 4000, "percentage": 1, "nextResetTime": 1788073095998 },
          { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 100, "nextResetTime": 1787056863927 },
          { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 20, "nextResetTime": 1787641095989 }
        ]
      }
    }
    """;

    private static void TestGlm(ref int fails)
    {
        var report = GlmProvider.Parse(ParseJson(GlmV2Json));

        Assert(ref fails, "glm.planName", report.PlanName, "max");
        Assert(ref fails, "glm.5h.percent", Find(report, "5h 滚动窗口")?.Percent, 100.0);
        Assert(ref fails, "glm.week.percent", Find(report, "周窗口")?.Percent, 20.0);
        Assert(ref fails, "glm.timeLimitIsDetail", Find(report, "MCP 月请求（Web Search / Web Reader / Zread）")?.IsDetail, true);

        const string creditJson = """
        {
          "success": true,
          "data": {
            "level": "pro",
            "limits": [
              { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "currentValue": 200, "usage": 1000, "nextResetTime": 1789000000000 },
              { "type": "CREDIT_LIMIT", "unit": 6, "number": 1, "currentValue": 156, "usage": 300, "nextResetTime": 1789600000000 },
              { "type": "CREDIT_LIMIT", "unit": 5, "number": 1, "currentValue": 0, "usage": 500, "nextResetTime": 1790200000000 }
            ]
          }
        }
        """;
        var credit = GlmProvider.Parse(ParseJson(creditJson));
        Assert(ref fails, "glm.credit.5h.ratio", Find(credit, "5h 滚动窗口")?.Percent, 20.0);
        Assert(ref fails, "glm.credit.week.ratio", Find(credit, "周窗口")?.Percent, 52.0);
        Assert(ref fails, "glm.credit.month.zero", Find(credit, "月窗口")?.Percent, 0.0);
        Assert(ref fails, "glm.credit.month.resetMs", Find(credit, "月窗口")?.ResetMs, 1790200000000L);

        var failed = AssertThrowsApi(ref fails, "glm.success=false", () =>
            GlmProvider.Parse(ParseJson("""{ "success": false, "msg": "invalid token" }""")));
        AssertContains(ref fails, "glm.success=false.message", failed, "invalid token");
    }

    private const string MinimaxJson = """
    {
      "model_remains": [
        {
          "model_name": "general",
          "current_interval_remaining_percent": 62,
          "current_interval_total_count": 1500,
          "current_weekly_remaining_percent": 80,
          "current_weekly_status": 1,
          "current_weekly_total_count": 15000,
          "end_time": "2026-09-09T12:00:00Z",
          "weekly_end_time": 1789600000000
        }
      ]
    }
    """;

    private static void TestMinimax(ref int fails)
    {
        var report = MinimaxProvider.Parse(ParseJson(MinimaxJson));
        Assert(ref fails, "minimax.windowCount", report.Windows.Count, 2);

        var fiveHour = Find(report, "5h 滚动窗口");
        Assert(ref fails, "minimax.5h.percent", fiveHour?.Percent, 38.0);
        Assert(ref fails, "minimax.5h.limit", fiveHour?.Limit, 1500.0);
        Assert(ref fails, "minimax.5h.used", fiveHour?.Used, 570.0);

        var weekly = Find(report, "周窗口");
        Assert(ref fails, "minimax.week.percent", weekly?.Percent, 20.0);
        Assert(ref fails, "minimax.week.resetMs", weekly?.ResetMs, 1789600000000L);

        const string weeklyDisabledJson = """
        { "model_remains": [ { "model_name": "general", "current_interval_remaining_percent": 50, "current_weekly_remaining_percent": 0, "current_weekly_status": 2 } ] }
        """;
        var disabled = MinimaxProvider.Parse(ParseJson(weeklyDisabledJson));
        Assert(ref fails, "minimax.week.disabled", disabled.Windows.Count, 1);

        // 真实响应同时带 video 等非编程额度，只应取 general 行
        const string mixedModelsJson = """
        {
          "model_remains": [
            { "model_name": "general", "current_interval_remaining_percent": 25 },
            { "model_name": "video", "current_interval_remaining_percent": 90 }
          ]
        }
        """;
        var mixed = MinimaxProvider.Parse(ParseJson(mixedModelsJson));
        Assert(ref fails, "minimax.onlyGeneral.count", mixed.Windows.Count, 1);
        Assert(ref fails, "minimax.onlyGeneral.percent", mixed.Windows[0].Percent, 75.0);
        Assert(ref fails, "minimax.onlyGeneral.label", mixed.Windows[0].Label, "5h 滚动窗口");
    }

    private const string OpenCodeJson = """
    {
      "usage": {
        "rolling": { "percent": 12.5, "resetsAt": "2026-09-09T12:00:00Z" },
        "weekly":  { "percent": 40,   "resetsAt": 1789600000000 },
        "monthly": { "percent": 0,    "resetsAt": 1790200000000 }
      }
    }
    """;

    private static void TestOpenCode(ref int fails)
    {
        var report = OpenCodeProvider.Parse(ParseJson(OpenCodeJson));
        Assert(ref fails, "opencode.windowCount", report.Windows.Count, 3);
        Assert(ref fails, "opencode.rolling.percent", Find(report, "5h 滚动窗口")?.Percent, 12.5);
        Assert(ref fails, "opencode.weekly.percent", Find(report, "周窗口")?.Percent, 40.0);
        // percent=0 的 resetsAt 是占位值，必须丢弃
        Assert(ref fails, "opencode.monthly.resetMs", Find(report, "月窗口")?.ResetMs, 0L);

        Assert(ref fails, "opencode.url", OpenCodeProvider.BuildUrl(new ProviderConfig { Token = "x" }),
            "https://opencode.ai/zen/go/v1/usage");
        Assert(ref fails, "opencode.url.baseUrl", OpenCodeProvider.BuildUrl(new ProviderConfig { Token = "x", BaseUrl = "https://proxy.local/zen/go/v1" }),
            "https://proxy.local/zen/go/v1/usage");
    }

    private const string CodexJson = """
    {
      "plan_type": "plus",
      "rate_limit": {
        "primary_window":   { "used_percent": 31, "limit_window_seconds": 18000,   "reset_at": 1789000000000 },
        "secondary_window": { "used_percent": 64, "limit_window_seconds": 604800,  "reset_at": 1789600000000 }
      }
    }
    """;

    private static void TestCodex(ref int fails)
    {
        var report = CodexProvider.Parse(ParseJson(CodexJson));
        Assert(ref fails, "codex.planName", report.PlanName, "plus");
        Assert(ref fails, "codex.primary.5h", Find(report, "5h 滚动窗口")?.Percent, 31.0);
        Assert(ref fails, "codex.secondary.week", Find(report, "周窗口")?.Percent, 64.0);
        Assert(ref fails, "codex.primary.resetMs", Find(report, "5h 滚动窗口")?.ResetMs, 1789000000000L);

        // 额度用尽时给出明确提示（真实 Pro 响应是周窗口 + limit_reached）
        const string reachedJson = """
        {
          "plan_type": "pro",
          "rate_limit": {
            "allowed": false,
            "limit_reached": true,
            "primary_window": { "used_percent": 100, "limit_window_seconds": 604800, "reset_at": 1793599953 },
            "secondary_window": null
          }
        }
        """;
        var reached = CodexProvider.Parse(ParseJson(reachedJson));
        Assert(ref fails, "codex.reached.windowCount", reached.Windows.Count, 1);
        Assert(ref fails, "codex.reached.week.percent", Find(reached, "周窗口")?.Percent, 100.0);
        Assert(ref fails, "codex.reached.resetMs", Find(reached, "周窗口")?.ResetMs, 1793599953000L);
        AssertContains(ref fails, "codex.reached.note", reached.Note ?? string.Empty, "已用尽");
    }

    private static void TestCodexAuth(ref int fails)
    {
        // ChatGPT 登录态：直接给 account_id
        const string chatgptAuth = """
        { "auth_mode": "chatgpt", "tokens": { "access_token": "tok-123", "account_id": "acct-1" } }
        """;
        Assert(ref fails, "codex.auth.parsed", CodexProvider.ParseAuthDocument(chatgptAuth, out var access, out var account), true);
        Assert(ref fails, "codex.auth.accessToken", access, "tok-123");
        Assert(ref fails, "codex.auth.accountId", account, "acct-1");

        // auth_mode=apikey 是纯 API key 登录，没有订阅用量
        const string apiKeyAuth = """
        { "auth_mode": "apikey", "OPENAI_API_KEY": "sk-xxx", "tokens": { "access_token": "tok-123" } }
        """;
        Assert(ref fails, "codex.auth.apikeyRejected", CodexProvider.ParseAuthDocument(apiKeyAuth, out _, out _), false);

        // account_id 缺省时从 id_token 的 JWT payload 取
        var payload = """{"https://api.openai.com/auth":{"chatgpt_account_id":"acct-jwt"}}""";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwtAuth = $$"""
        { "tokens": { "access_token": "tok-456", "id_token": "header.{{encoded}}.signature" } }
        """;
        Assert(ref fails, "codex.auth.jwt.parsed", CodexProvider.ParseAuthDocument(jwtAuth, out var jwtAccess, out var jwtAccount), true);
        Assert(ref fails, "codex.auth.jwt.accessToken", jwtAccess, "tok-456");
        Assert(ref fails, "codex.auth.jwt.accountId", jwtAccount, "acct-jwt");

        Assert(ref fails, "codex.auth.noToken", CodexProvider.ParseAuthDocument("""{ "tokens": {} }""", out _, out _), false);
        Assert(ref fails, "codex.auth.invalidJson", CodexProvider.ParseAuthDocument("not json", out _, out _), false);

        // config 已配置 token 时不被本机登录态覆盖
        var configured = new ProviderConfig { Token = "explicit", AccountId = "acct-explicit" };
        new CodexProvider().ApplyAmbientCredentials(configured);
        Assert(ref fails, "codex.auth.explicitKept", configured.Token, "explicit");
    }

    private static void TestBalanceParsers(ref int fails)
    {
        var deepSeek = BalanceProviders.ParseDeepSeek(ParseJson("""
        {
          "is_available": true,
          "balance_infos": [
            { "currency": "CNY", "total_balance": "110.50", "topped_up_balance": "100.00", "granted_balance": "10.50" }
          ]
        }
        """));
        Assert(ref fails, "deepseek.currency", deepSeek.Currency, "CNY");
        Assert(ref fails, "deepseek.available", deepSeek.Available, "110.50");
        AssertContains(ref fails, "deepseek.toppedUp", deepSeek.Lines[0].Value, "100.00");

        var moonshotCn = BalanceProviders.ParseMoonshot(ParseJson("""
        { "data": { "available_balance": 42.5, "cash_balance": 40, "voucher_balance": 2.5 } }
        """), new ProviderConfig { Token = "x" });
        Assert(ref fails, "moonshot.cn.currency", moonshotCn.Currency, "CNY");
        Assert(ref fails, "moonshot.cn.available", moonshotCn.Available, "42.50");

        var moonshotGlobal = BalanceProviders.ParseMoonshot(ParseJson("""
        { "data": { "available_balance": 7 } }
        """), new ProviderConfig { Token = "x", Region = "global" });
        Assert(ref fails, "moonshot.global.currency", moonshotGlobal.Currency, "USD");

        var openRouter = BalanceProviders.ParseOpenRouter(ParseJson("""
        { "data": { "total_credits": 20, "total_usage": 5.25 } }
        """));
        Assert(ref fails, "openrouter.currency", openRouter.Currency, "USD");
        Assert(ref fails, "openrouter.available", openRouter.Available, "14.75");

        var siliconFlow = BalanceProviders.ParseSiliconFlow(ParseJson("""
        { "data": { "totalBalance": "88.00", "chargeBalance": "80.00", "balance": "8.00" } }
        """), new ProviderConfig { Token = "x" });
        Assert(ref fails, "siliconflow.currency", siliconFlow.Currency, "CNY");
        Assert(ref fails, "siliconflow.available", siliconFlow.Available, "88.00");

        var zenmux = BalanceProviders.ParseZenMux(ParseJson("""
        { "data": { "total_credits": 3.5 } }
        """));
        Assert(ref fails, "zenmux.currency", zenmux.Currency, "USD");
        Assert(ref fails, "zenmux.available", zenmux.Available, "3.50");
    }

    private static void TestTimeUtil(ref int fails)
    {
        Assert(ref fails, "time.epochMs", TimeUtil.FromNumber(1_788_000_000_000d), 1_788_000_000_000L);
        Assert(ref fails, "time.epochSeconds", TimeUtil.FromNumber(1_788_000_000d), 1_788_000_000_000L);
        Assert(ref fails, "time.zero", TimeUtil.FromNumber(0), 0L);
        Assert(ref fails, "time.negative", TimeUtil.FromNumber(-1), 0L);
        Assert(ref fails, "time.iso", TimeUtil.ParseInstantMs("2026-09-09T12:00:00Z"), 1_788_955_200_000L);
        Assert(ref fails, "time.numericString", TimeUtil.ParseInstantMs("1788000000"), 1_788_000_000_000L);
        Assert(ref fails, "time.garbage", TimeUtil.ParseInstantMs("not-a-time"), 0L);
        // now+5h 截断到分钟时可能落到 4 小时 59 分，两种都算通过
        var countdown = TimeUtil.FormatReset(DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeMilliseconds());
        Assert(ref fails, "time.countdownHours",
            countdown is "5 小时 0 分后重置" or "4 小时 59 分后重置", true);
    }

    private static void TestRenderer(ref int fails)
    {
        var plan = ReportRenderer.RenderPlan("标题", new PlanReport
        {
            PlanName = "max",
            Windows = new List<PlanWindow>
            {
                new() { Label = "5h 滚动窗口", Percent = 12, ResetMs = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeMilliseconds() },
                new() { Label = "月度总额度", Percent = 61, Used = 610, Limit = 1000, ResetMs = DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeMilliseconds() },
                new() { Label = "周窗口", Percent = 100, ResetMs = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeMilliseconds() },
                new() { Label = "其中 Kimi Code", Percent = 18, IsDetail = true },
            },
        });
        AssertContains(ref fails, "render.planName", plan, "套餐: max");
        AssertContains(ref fails, "render.5h", plan, "[5h 滚动窗口]");
        AssertContains(ref fails, "render.month", plan, "[月度总额度]");
        // 与官网一致：剩余为主、已用为辅
        AssertContains(ref fails, "render.remainingFirst", plan, "剩余 88% · 已用 12%");
        AssertContains(ref fails, "render.usedLabel", plan, "已用 12%");
        AssertContains(ref fails, "render.remainingLabel", plan, "剩余 88%");
        // 用尽时条形图必须是空的（满格代表剩余充足）
        AssertContains(ref fails, "render.exhausted", plan, "剩余 0% · 已用 100%");
        Assert(ref fails, "render.bar.exhausted", ReportRenderer.BuildBar(0), new string('░', 20));
        Assert(ref fails, "render.bar.full", ReportRenderer.BuildBar(100), new string('█', 20));
        AssertContains(ref fails, "render.detail", plan, "└ 其中 Kimi Code: 已用 18%");
        AssertContains(ref fails, "render.countdown", plan, "小时后重置");
        AssertNotContains(ref fails, "render.no-huge-days", plan, "20724 天");

        var balance = ReportRenderer.RenderBalance("余额", new BalanceReport
        {
            Currency = "CNY",
            Available = "110.50",
            Lines = new List<BalanceLine> { new() { Label = "充值余额", Value = "¥100.00" } },
        });
        AssertContains(ref fails, "render.balance", balance, "可用余额: ¥110.50");
        AssertContains(ref fails, "render.balance.line", balance, "充值余额: ¥100.00");
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static PlanWindow? Find(PlanReport report, string label) =>
        report.Windows.FirstOrDefault(window => window.Label == label);

    private static string AssertThrowsApi(ref int fails, string label, Action action)
    {
        try
        {
            action();
        }
        catch (ProviderException ex) when (ex.Kind == ProviderExceptionKind.ApiError)
        {
            Console.WriteLine($"  [ OK ] {label}");
            return ex.Message;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [FAIL] {label}: unexpected {ex.GetType().Name}: {ex.Message}");
            fails++;
            return string.Empty;
        }

        Console.Error.WriteLine($"  [FAIL] {label}: expected ProviderException(ApiError)");
        fails++;
        return string.Empty;
    }

    private static void Assert(ref int fails, string label, object? actual, object? expected)
    {
        var equal = (actual is null && expected is null) || (actual is not null && actual.Equals(expected));
        if (!equal)
        {
            Console.Error.WriteLine($"  [FAIL] {label}: expected={expected ?? "null"}, actual={actual ?? "null"}");
            fails++;
        }
        else
        {
            Console.WriteLine($"  [ OK ] {label} = {actual ?? "null"}");
        }
    }

    private static void AssertContains(ref int fails, string label, string haystack, string needle)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"  [FAIL] {label}: '{needle}' not in '{haystack.Replace("\n", "\\n")}'");
            fails++;
        }
        else
        {
            Console.WriteLine($"  [ OK ] {label}");
        }
    }

    private static void AssertNotContains(ref int fails, string label, string haystack, string needle)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"  [FAIL] {label}: unexpected '{needle}' in '{haystack.Replace("\n", "\\n")}'");
            fails++;
        }
        else
        {
            Console.WriteLine($"  [ OK ] {label}");
        }
    }
}
