using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TokenUsage.Errors;

namespace TokenUsage.Providers.Glm;

internal sealed class GlmProvider : IProvider
{
    public string Name => "glm";
    public string DisplayTitle => "GLM Token Plan 用量";

    // 智谱 BigModel / Z.AI 配额查询端点
    // （与 dsh-glm-usage lib/index.js、pi-glm-quota pure.ts、opencodex quota.ts 保持一致）
    private const string HostCn = "https://open.bigmodel.cn";
    private const string HostGlobal = "https://api.z.ai";
    private const string MonitorPath = "/api/monitor/usage/quota/limit";

    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        return client;
    }

    public async Task<string> FetchAndRenderAsync(string token, CancellationToken cancellationToken)
    {
        // 智谱两种鉴权方式都被不同客户端使用过：
        //   - opencodex / z.ai 官方: `Authorization: Bearer <key>`
        //   - dsh-glm-usage / pi-glm-quota: 直接用 key 作值（无 Bearer 前缀）
        // 两条路径都先尝试，第一个非 401/403 的视为成功。
        var result = await TryProbeAsync(token, bearer: true, cancellationToken)
            ?? await TryProbeAsync(token, bearer: false, cancellationToken);

        if (result is null)
        {
            // TryProbeAsync 在失败时已抛 ProviderException；如果静默走到 null，说明两次都不可达
            throw new ProviderException(ProviderExceptionKind.Unauthorized, "token 无效或已过期，请登录 https://open.bigmodel.cn 重置 API Key");
        }

        var usage = GlmUsage.Parse(result.Value.Data, result.Value.Host);
        return GlmRenderer.Render(usage);
    }

    private static async Task<(QuotaData Data, string Host)?> TryProbeAsync(
        string token, bool bearer, CancellationToken cancellationToken)
    {
        var hosts = new[] { HostCn, HostGlobal };
        Exception? lastError = null;

        foreach (var host in hosts)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, host + MonitorPath);
            var headerValue = bearer ? $"Bearer {token}" : token;
            request.Headers.TryAddWithoutValidation("Authorization", headerValue);

            HttpResponseMessage response;
            try
            {
                response = await SharedClient.SendAsync(request, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderException(ProviderExceptionKind.Timeout, "请求超时（15秒）");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // 401/403 当前鉴权形式不通过，换一种形式 / 另一个 host 再试
            if ((int)response.StatusCode is 401 or 403)
            {
                response.Dispose();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var preview = body.Length > 200 ? body[..200] : body;
                throw new ProviderException(
                    ProviderExceptionKind.ApiError,
                    $"API 返回 {(int)response.StatusCode} {response.ReasonPhrase}：{preview}");
            }

            QuotaResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(body, GlmJsonContext.Default.QuotaResponse);
            }
            catch (JsonException ex)
            {
                lastError = ex;
                continue;
            }

            if (parsed is null)
            {
                lastError = new InvalidOperationException("响应为空");
                continue;
            }

            if (parsed.Success is false)
            {
                throw new ProviderException(
                    ProviderExceptionKind.ApiError,
                    string.IsNullOrWhiteSpace(parsed.Msg) ? "业务错误" : parsed.Msg!);
            }

            if (parsed.Data is null)
            {
                lastError = new InvalidOperationException("响应缺少 data 字段");
                continue;
            }

            return (parsed.Data, host);
        }

        if (lastError is not null)
        {
            throw new ProviderException(ProviderExceptionKind.Network, $"网络异常: {lastError.Message}");
        }
        return null;
    }
}

// 服务端的限额条目；兼容 TOKENS_LIMIT（旧协议）和 CREDIT_LIMIT（新积分套餐协议）
internal sealed class QuotaResponse
{
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("code")]
    public long? Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public QuotaData? Data { get; set; }
}

internal sealed class QuotaData
{
    [JsonPropertyName("level")]
    public string? Level { get; set; }

    [JsonPropertyName("limits")]
    public List<QuotaLimit> Limits { get; set; } = new();
}

internal sealed class QuotaLimit
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("unit")]
    public long? Unit { get; set; }

    [JsonPropertyName("number")]
    public long? Number { get; set; }

    [JsonPropertyName("usage")]
    public double? Usage { get; set; }

    [JsonPropertyName("currentValue")]
    public double? CurrentValue { get; set; }

    [JsonPropertyName("remaining")]
    public double? Remaining { get; set; }

    [JsonPropertyName("percentage")]
    public double? Percentage { get; set; }

    [JsonPropertyName("nextResetTime")]
    public long? NextResetTime { get; set; }
}

[JsonSerializable(typeof(QuotaResponse))]
internal partial class GlmJsonContext : JsonSerializerContext
{
}