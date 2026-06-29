using System.Text.Json;
using System.Text.Json.Serialization;
using TokenUsage.Errors;

namespace TokenUsage.Config;

internal static partial class ConfigLoader
{
    public sealed class Root
    {
        [JsonPropertyName("providers")]
        public Dictionary<string, ProviderConfig>? Providers { get; set; }
    }

    public sealed class ProviderConfig
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }

    public sealed class LoadResult
    {
        public Dictionary<string, string> Tokens { get; init; } = new(StringComparer.Ordinal);
        public string? Error { get; init; }
        public string? Detail { get; init; }
    }

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

        Root? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Root);
        }
        catch (JsonException ex)
        {
            return new LoadResult
            {
                Error = $"config.json 解析失败: {ex.Message}",
                Detail = $"路径: {configPath}"
            };
        }

        if (parsed is null || parsed.Providers is null || parsed.Providers.Count == 0)
        {
            return new LoadResult
            {
                Error = "config.json 缺少 providers 配置或所有 provider 都为空",
                Detail = $"路径: {configPath}；新 schema 仅支持嵌套 providers.<name>.token 格式（不再兼容旧的顶层 token 字段）"
            };
        }

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, cfg) in parsed.Providers)
        {
            if (cfg is null) continue;
            if (string.IsNullOrWhiteSpace(cfg.Token)) continue;
            tokens[name] = cfg.Token.Trim();
        }

        if (tokens.Count == 0)
        {
            return new LoadResult
            {
                Error = "config.json 中所有 provider 的 token 字段都为空",
                Detail = $"路径: {configPath}"
            };
        }

        return new LoadResult { Tokens = tokens };
    }

    [JsonSerializable(typeof(Root))]
    internal partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}