using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenUsage.Config;

internal sealed class ConfigRoot
{
    [JsonPropertyName("providers")]
    public Dictionary<string, ProviderConfig?>? Providers { get; set; }
}

internal sealed class ProviderConfig
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>"cn"（默认）或 "global"/"intl"；用于区分国内/国际站点。</summary>
    [JsonPropertyName("region")]
    public string? Region { get; set; }

    /// <summary>Codex (ChatGPT 订阅) 的 chatgpt-account-id，可选。</summary>
    [JsonPropertyName("accountId")]
    public string? AccountId { get; set; }

    /// <summary>可选的自定义端点基址（代理 / 兼容网关）。</summary>
    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token);

    public string EffectiveRegion =>
        string.IsNullOrWhiteSpace(Region) ? "cn" : Region.Trim().ToLowerInvariant();

    public bool IsGlobalRegion => EffectiveRegion is "global" or "intl" or "international" or "us";
}

internal sealed class LoadResult
{
    public Dictionary<string, ProviderConfig> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; init; }
    public string? Detail { get; init; }
}

internal static partial class ConfigLoader
{
    public static LoadResult Load(string exeDir)
    {
        var configPath = Path.Combine(exeDir, "config.json");

        if (!File.Exists(configPath))
        {
            return new LoadResult
            {
                Error = $"未找到配置文件: {configPath}",
                Detail = "请创建 <exe-dir>/config.json（参考 config.json.template），或使用 <子命令> --token <value> 直接传入"
            };
        }

        string json;
        try
        {
            json = File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            return new LoadResult
            {
                Error = $"读取 config.json 失败: {ex.Message}",
                Detail = $"路径: {configPath}"
            };
        }

        ConfigRoot? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.ConfigRoot);
        }
        catch (JsonException ex)
        {
            return new LoadResult
            {
                Error = $"config.json 解析失败: {ex.Message}",
                Detail = $"路径: {configPath}"
            };
        }

        if (parsed?.Providers is null || parsed.Providers.Count == 0)
        {
            return new LoadResult
            {
                Error = "config.json 缺少 providers 配置",
                Detail = $"路径: {configPath}；请使用 providers.<name>.token 格式（参考 config.json.template）"
            };
        }

        var providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, cfg) in parsed.Providers)
        {
            if (cfg is null || !cfg.IsConfigured) continue;
            cfg.Token = cfg.Token!.Trim();
            providers[name] = cfg;
        }

        // all 模式下未配置的 provider 会被跳过，因此这里不把“全部为空”当成读取失败：
        // 单 provider 子命令或 --token 覆盖仍可正常工作。
        return new LoadResult { Providers = providers };
    }

    [JsonSerializable(typeof(ConfigRoot))]
    internal partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}
