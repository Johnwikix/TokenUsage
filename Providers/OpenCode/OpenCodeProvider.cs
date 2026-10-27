using System.Text.Json;
using TokenUsage.Config;
using TokenUsage.Providers.Common;

namespace TokenUsage.Providers.OpenCode;

internal sealed class OpenCodeProvider : IProvider
{
    public string Name => "opencode";
    public string DisplayTitle => "OpenCode Go 套餐用量";

    private const string DefaultUrl = "https://opencode.ai/zen/go/v1/usage";
    private const string AuthHint = "token 无效或已过期，请登录 https://opencode.ai 重置 API Key";

    public async Task<string> FetchAndRenderAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        var url = BuildUrl(config);

        var (document, _) = await HttpProbe.GetJsonAnyAsync(
            new[] { new Endpoint(url) },
            config.Token!,
            null,
            TimeSpan.FromSeconds(15),
            AuthHint,
            cancellationToken);

        using (document)
        {
            return ReportRenderer.RenderPlan(DisplayTitle, Parse(document.RootElement));
        }
    }

    internal static string BuildUrl(ProviderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.BaseUrl)) return DefaultUrl;

        var baseUrl = config.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/usage", StringComparison.OrdinalIgnoreCase)) return baseUrl;
        if (baseUrl.EndsWith("/zen/go/v1", StringComparison.OrdinalIgnoreCase)) return baseUrl + "/usage";
        if (baseUrl.EndsWith("/zen/go", StringComparison.OrdinalIgnoreCase)) return baseUrl + "/v1/usage";
        return baseUrl + "/zen/go/v1/usage";
    }

    /// <summary>
    /// OpenCode Go 只给百分比：usage.rolling / weekly / monthly。
    /// percent=0 时 resetsAt 是占位值（now + 窗口），丢弃以免误导。
    /// </summary>
    internal static PlanReport Parse(JsonElement root)
    {
        var usage = JsonUtil.Prop(root, "usage")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 usage");

        var windows = new List<PlanWindow>();
        AddWindow(usage, "rolling", "5h 滚动窗口");
        AddWindow(usage, "weekly", "周窗口");
        AddWindow(usage, "monthly", "月窗口");

        return new PlanReport
        {
            Windows = windows,
            Note = windows.Count == 0 ? "未返回 rolling / weekly / monthly 窗口" : null,
        };

        void AddWindow(JsonElement usageRoot, string field, string label)
        {
            if (JsonUtil.Prop(usageRoot, field) is not { ValueKind: JsonValueKind.Object } entry) return;
            var percent = JsonUtil.Num(entry, "percent");
            windows.Add(new PlanWindow
            {
                Label = label,
                Percent = percent is null ? null : Math.Clamp(percent.Value, 0, 100),
                ResetMs = percent is { } p && p == 0
                    ? 0
                    : TimeUtil.ParseInstantMs(JsonUtil.FirstProp(entry, "resetsAt", "resets_at", "resetAt")),
            });
        }
    }
}
