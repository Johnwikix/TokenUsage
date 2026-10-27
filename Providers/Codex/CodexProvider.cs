using System.Text.Json;
using TokenUsage.Config;
using TokenUsage.Providers.Common;

namespace TokenUsage.Providers.Codex;

internal sealed class CodexProvider : IProvider
{
    public string Name => "codex";
    public string DisplayTitle => "Codex (ChatGPT 订阅) 用量";

    private const string DefaultUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string AuthHint =
        "Codex 用量走 ChatGPT 登录态而不是 API key：请先运行 codex login（或用 Codex 跑一次请求刷新 token），" +
        "工具会自动读取 ~/.codex/auth.json；也可在 config.json 的 providers.codex.token 手动指定";

    /// <summary>
    /// ChatGPT Plus/Pro 订阅没有 API key，Codex CLI 登录后把 OAuth 凭证写在 auth.json。
    /// config.json 未配置 token 时直接复用这份登录态（含 account_id 请求头）。
    /// </summary>
    public void ApplyAmbientCredentials(ProviderConfig config)
    {
        if (config.IsConfigured) return;
        if (!TryLoadLocalAuth(out var accessToken, out var accountId, out _)) return;

        config.Token = accessToken;
        if (string.IsNullOrWhiteSpace(config.AccountId)) config.AccountId = accountId;
    }

    public async Task<string> FetchAndRenderAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        var token = config.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ProviderException(ProviderExceptionKind.Unauthorized, AuthHint);
        }

        var headers = new Dictionary<string, string>
        {
            ["user-agent"] = "codex-cli",
            ["Accept"] = "application/json",
        };
        if (!string.IsNullOrWhiteSpace(config.AccountId))
        {
            headers["chatgpt-account-id"] = config.AccountId.Trim();
        }

        var url = string.IsNullOrWhiteSpace(config.BaseUrl) ? DefaultUrl : config.BaseUrl.Trim();
        var (document, _) = await HttpProbe.GetJsonAnyAsync(
            new[] { new Endpoint(url) },
            token,
            headers,
            TimeSpan.FromSeconds(20),
            AuthHint,
            cancellationToken);

        using (document)
        {
            return ReportRenderer.RenderPlan(DisplayTitle, Parse(document.RootElement));
        }
    }

    /// <summary>依次查 $CODEX_HOME/auth.json 与 ~/.codex/auth.json。</summary>
    private static IEnumerable<string> AuthCandidates()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            yield return Path.Combine(codexHome.Trim(), "auth.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".codex", "auth.json");
        }
    }

    internal static bool TryLoadLocalAuth(out string? accessToken, out string? accountId, out string? source)
    {
        accessToken = null;
        accountId = null;
        source = null;

        foreach (var path in AuthCandidates())
        {
            if (!File.Exists(path)) continue;

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (!ParseAuthDocument(json, out var token, out var account)) continue;

            accessToken = token;
            accountId = account;
            source = path;
            return true;
        }

        return false;
    }

    /// <summary>
    /// auth.json 里 auth_mode=apikey 表示用 OPENAI_API_KEY 登录，没有订阅用量可查；
    /// 只有 chatgpt 登录态（tokens.access_token）才能读 wham/usage。
    /// </summary>
    internal static bool ParseAuthDocument(string json, out string? accessToken, out string? accountId)
    {
        accessToken = null;
        accountId = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (JsonUtil.Str(root, "auth_mode") is { } mode
                && mode.Equals("apikey", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (JsonUtil.Prop(root, "tokens") is not { ValueKind: JsonValueKind.Object } tokens) return false;

            var token = JsonUtil.Str(tokens, "access_token");
            if (string.IsNullOrWhiteSpace(token)) return false;
            accessToken = token;

            var account = JsonUtil.Str(tokens, "account_id");
            if (string.IsNullOrWhiteSpace(account))
            {
                account = AccountIdFromIdToken(JsonUtil.Str(tokens, "id_token"));
            }
            accountId = string.IsNullOrWhiteSpace(account) ? null : account;
            return true;
        }
    }

    /// <summary>auth.json 未直接给 account_id 时，从 id_token 的 JWT payload 里取。</summary>
    private static string? AccountIdFromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;

            if (JsonUtil.Prop(root, "https://api.openai.com/auth") is { ValueKind: JsonValueKind.Object } auth
                && JsonUtil.Str(auth, "chatgpt_account_id") is { Length: > 0 } nested)
            {
                return nested;
            }

            return JsonUtil.Str(root, "chatgpt_account_id");
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static PlanReport Parse(JsonElement root)
    {
        var rateLimit = JsonUtil.Prop(root, "rate_limit")
            ?? throw new ProviderException(ProviderExceptionKind.ApiError, "响应缺少 rate_limit");

        var windows = new List<PlanWindow>();
        AddWindow(rateLimit, "primary_window");
        AddWindow(rateLimit, "secondary_window");

        if (windows.Count == 0)
        {
            throw new ProviderException(
                ProviderExceptionKind.ApiError,
                "响应缺少 rate_limit.primary_window / secondary_window");
        }

        var planName = JsonUtil.FirstStr(root, "plan_type", "planType", "plan");

        var limitReached = JsonUtil.Bool(rateLimit, "limit_reached") == true
            || JsonUtil.Bool(rateLimit, "allowed") == false;

        return new PlanReport
        {
            PlanName = planName,
            Windows = windows,
            Note = limitReached ? "本周期额度已用尽，重置后自动恢复；额度不足时可在 ChatGPT 中加购 credits。" : null,
        };

        void AddWindow(JsonElement limit, string field)
        {
            if (JsonUtil.Prop(limit, field) is not { ValueKind: JsonValueKind.Object } window) return;
            var percent = JsonUtil.Num(window, "used_percent");
            if (percent is null) return;

            var seconds = JsonUtil.Num(window, "limit_window_seconds");
            windows.Add(new PlanWindow
            {
                Label = WindowLabel(seconds),
                Percent = Math.Clamp(percent.Value, 0, 100),
                ResetMs = TimeUtil.ParseInstantMs(JsonUtil.FirstProp(window, "reset_at", "resetAt")),
            });
        }
    }

    private static string WindowLabel(double? seconds)
    {
        if (seconds is null or <= 0) return "额度窗口";
        return seconds.Value switch
        {
            18_000d => "5h 滚动窗口",
            604_800d => "周窗口",
            2_592_000d => "月窗口",
            _ when seconds.Value >= 86_400d => $"{Math.Round(seconds.Value / 86_400d)} 天窗口",
            _ => $"{Math.Round(seconds.Value / 3_600d)} 小时窗口",
        };
    }
}
