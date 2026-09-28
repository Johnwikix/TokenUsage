using System.Text.Json;

namespace TokenUsage.Providers.Glm;

/// <summary>
/// 解析逻辑的离线自检 — 直接对 GlmUsage.Parse 喂入典型响应体，
/// 验证等级 / 三类窗口（5h / 周 / 月 MCP）都被正确分类，
/// 并验证 nextResetTime 的 unix epoch ms 归一化。
/// 真实 API 路径由 TokenUsage.exe glm 实跑覆盖。
/// </summary>
internal static class GlmUsageTests
{
    public static int Run()
    {
        var fails = 0;

        // 案例 1：v2 协议响应（来自 opencodex 真实捕获）
        // level=max，含 TIME_LIMIT 月 MCP 行，TOKENS_LIMIT 5h/周
        // nextResetTime 是 unix epoch 毫秒
        var v2Json = """
        {
            "limits": [
                { "type": "TIME_LIMIT", "unit": 5, "number": 1, "usage": 4000, "currentValue": 0, "remaining": 4000, "percentage": 0, "nextResetTime": 1788073095998 },
                { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 100, "nextResetTime": 1787056863927 },
                { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 20, "nextResetTime": 1787641095989 }
            ],
            "level": "max"
        }
        """;
        var v2Data = JsonSerializer.Deserialize(v2Json, GlmJsonContext.Default.QuotaData)!;
        var v2Usage = GlmUsage.Parse(v2Data, "https://open.bigmodel.cn");
        Assert(ref fails, "v2.level", v2Usage.Level, "max");
        Assert(ref fails, "v2.fiveHour.percent", v2Usage.FiveHour?.Percent, 100.0);
        Assert(ref fails, "v2.fiveHour.resetMs", v2Usage.FiveHour?.ResetMs, 1787056863927L);
        Assert(ref fails, "v2.weekly.percent", v2Usage.Weekly?.Percent, 20.0);
        Assert(ref fails, "v2.weekly.resetMs", v2Usage.Weekly?.ResetMs, 1787641095989L);
        Assert(ref fails, "v2.monthly.percent", v2Usage.Monthly?.Percent, 0.0);

        // 案例 2：新协议响应（仅 CREDIT_LIMIT，无 TIME_LIMIT，无月度 MCP）
        var newProtoJson = """
        {
            "limits": [
                { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "usage": 12000, "currentValue": 0, "remaining": 12000, "percentage": 0 },
                { "type": "CREDIT_LIMIT", "unit": 6, "number": 1, "usage": 60000, "currentValue": 0, "remaining": 60000, "percentage": 0, "nextResetTime": 1787649214999 }
            ],
            "level": "pro"
        }
        """;
        var newProtoData = JsonSerializer.Deserialize(newProtoJson, GlmJsonContext.Default.QuotaData)!;
        var newProtoUsage = GlmUsage.Parse(newProtoData, "https://api.z.ai");
        Assert(ref fails, "newProto.level", newProtoUsage.Level, "pro");
        Assert(ref fails, "newProto.fiveHour.percent", newProtoUsage.FiveHour?.Percent, 0.0);
        Assert(ref fails, "newProto.weekly.percent", newProtoUsage.Weekly?.Percent, 0.0);
        Assert(ref fails, "newProto.weekly.resetMs", newProtoUsage.Weekly?.ResetMs, 1787649214999L);
        Assert(ref fails, "newProto.monthly must be null", newProtoUsage.Monthly is null, true);

        // 案例 3：unit/number 不匹配的窗口（应被忽略）
        var mismatchJson = """
        {
            "limits": [
                { "type": "TOKENS_LIMIT", "unit": 3, "number": 2, "percentage": 40, "nextResetTime": 1789000000000 },
                { "type": "TOKENS_LIMIT", "unit": 6, "number": 2, "percentage": 52, "nextResetTime": 1789600000000 },
                { "type": "TIME_LIMIT", "percentage": 12.3, "nextResetTime": 1789000000000 }
            ],
            "level": "?"
        }
        """;
        var mismatchData = JsonSerializer.Deserialize(mismatchJson, GlmJsonContext.Default.QuotaData)!;
        var mismatchUsage = GlmUsage.Parse(mismatchData, "https://api.z.ai");
        Assert(ref fails, "mismatch.fiveHour must be null", mismatchUsage.FiveHour is null, true);
        Assert(ref fails, "mismatch.weekly must be null", mismatchUsage.Weekly is null, true);
        Assert(ref fails, "mismatch.monthly.percent", mismatchUsage.Monthly?.Percent, 12.3);

        // 案例 4：percentage 缺位，靠 currentValue / usage 推导
        var deriveJson = """
        {
            "limits": [
                { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "currentValue": 200, "usage": 1000, "nextResetTime": 1789000000000 },
                { "type": "CREDIT_LIMIT", "unit": 6, "number": 1, "currentValue": 156, "usage": 300, "nextResetTime": 1789600000000 }
            ],
            "level": "pro"
        }
        """;
        var deriveData = JsonSerializer.Deserialize(deriveJson, GlmJsonContext.Default.QuotaData)!;
        var deriveUsage = GlmUsage.Parse(deriveData, "https://api.z.ai");
        Assert(ref fails, "derive.fiveHour.percent", deriveUsage.FiveHour?.Percent, 20.0);
        Assert(ref fails, "derive.weekly.percent", deriveUsage.Weekly?.Percent, 52.0);

        // 案例 5：空 limits（用户当前的真实场景如果响应只回 level）
        var emptyJson = """
        { "limits": [], "level": "max" }
        """;
        var emptyData = JsonSerializer.Deserialize(emptyJson, GlmJsonContext.Default.QuotaData)!;
        var emptyUsage = GlmUsage.Parse(emptyData, "https://open.bigmodel.cn");
        Assert(ref fails, "empty.all-null.5h", emptyUsage.FiveHour is null, true);
        Assert(ref fails, "empty.all-null.weekly", emptyUsage.Weekly is null, true);
        Assert(ref fails, "empty.all-null.monthly", emptyUsage.Monthly is null, true);

        // 案例 6：nextResetTime 归一化（unix epoch ms）— 必须按 1970 锚点算
        Assert(ref fails, "epochMs.ms form", GlmUsage.EpochMillis(1_788_000_000_000L), 1_788_000_000_000L);
        Assert(ref fails, "epochMs.s form", GlmUsage.EpochMillis(1_788_000_000L), 1_788_000_000_000L);
        Assert(ref fails, "epochMs.zero is sentinel", GlmUsage.EpochMillis(0), 0L);
        Assert(ref fails, "epochMs.negative is sentinel", GlmUsage.EpochMillis(-1), 0L);
        Assert(ref fails, "epochMs.null is sentinel", GlmUsage.EpochMillis(null), 0L);

        // 案例 7：renderer 倒计时（基于绝对 epoch 的差值）
        // 取 now + 5h 整 / +7d 整的 unix ms 当 resetMs，渲染应输出近似的倒计时
        var fiveHourLaterMs = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeMilliseconds();
        var rendered = GlmRenderer.RenderForTest(new GlmUsage
        {
            Host = "https://open.bigmodel.cn",
            FiveHour = new QuotaWindow { Label = "5h", Percent = 1, Used = 347, Total = 28000, ResetMs = fiveHourLaterMs },
            Weekly = new QuotaWindow { Label = "周", Percent = 73, Used = 102894, Total = 140000, ResetMs = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds() },
        });
        // 必须包含 "X 小时" 倒计时（不再出现 20724 天）
        AssertContains(ref fails, "render.5h.contains 小时", rendered, "小时");
        AssertContains(ref fails, "render.weekly.contains 天", rendered, "天");
        // 必须不再出现 "20724 天"
        AssertNotContains(ref fails, "render.no-20724", rendered, "20724 天");
        // 倒计时不能为零
        AssertNotContains(ref fails, "render.no-已重置", rendered, "已重置");
        // 倒计时必须落在合理范围：5h ≤ "X 小时" ≤ 8h；7d ≤ "X 天" ≤ 10d
        // （避免格式输出成秒数或别的单位）
        AssertContains(ref fails, "render.5h 单位正确", rendered, "后重置");
        AssertContains(ref fails, "render.weekly 单位正确", rendered, "后重置");

        return fails;
    }

    private static void Assert(ref int fails, string label, object? actual, object? expected)
    {
        var eq = (actual is null && expected is null) || (actual is not null && actual.Equals(expected));
        if (!eq)
        {
            System.Console.Error.WriteLine($"  [FAIL] {label}: expected={expected ?? "null"}, actual={actual ?? "null"}");
            fails++;
        }
        else
        {
            System.Console.WriteLine($"  [ OK ] {label} = {actual ?? "null"}");
        }
    }

    private static void AssertContains(ref int fails, string label, string haystack, string needle)
    {
        if (!haystack.Contains(needle))
        {
            System.Console.Error.WriteLine($"  [FAIL] {label}: '{needle}' not in '{haystack.Replace("\n", "\\n")}'");
            fails++;
        }
        else
        {
            System.Console.WriteLine($"  [ OK ] {label}");
        }
    }

    private static void AssertNotContains(ref int fails, string label, string haystack, string needle)
    {
        if (haystack.Contains(needle))
        {
            System.Console.Error.WriteLine($"  [FAIL] {label}: unexpected '{needle}' in '{haystack.Replace("\n", "\\n")}'");
            fails++;
        }
        else
        {
            System.Console.WriteLine($"  [ OK ] {label}");
        }
    }
}