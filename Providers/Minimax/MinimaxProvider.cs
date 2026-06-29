using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TokenUsage.Errors;

namespace TokenUsage.Providers.Minimax;

internal sealed class MinimaxProvider : IProvider
{
    public string Name => "minimax";
    public string DisplayTitle => "MiniMax Coding Plan 用量";

    private const string EndpointUrl = "https://www.minimaxi.com/v1/api/openplatform/coding_plan/remains";

    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<string> FetchAndRenderAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, EndpointUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Referrer = new Uri("https://platform.minimaxi.com/");

        HttpResponseMessage response;
        try
        {
            response = await SharedClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(ProviderExceptionKind.Timeout, "请求超时（10秒）");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(ProviderExceptionKind.Network, $"网络异常: {ex.Message}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 401)
            {
                throw new ProviderException(
                    ProviderExceptionKind.Unauthorized,
                    "token 无效或已过期，请登录 https://platform.minimaxi.com/user-center/payment/coding-plan 重置 API Key");
            }
            var preview = body.Length > 200 ? body[..200] : body;
            throw new ProviderException(
                ProviderExceptionKind.ApiError,
                $"API 返回 {(int)response.StatusCode} {response.ReasonPhrase}：{preview}");
        }

        CodingPlanResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, CodingPlanJsonContext.Default.CodingPlanResponse);
        }
        catch (JsonException ex)
        {
            throw new ProviderException(ProviderExceptionKind.ApiError, $"响应 JSON 解析失败: {ex.Message}");
        }

        if (parsed is null)
        {
            throw new ProviderException(ProviderExceptionKind.ApiError, "API 响应为空");
        }

        return MinimaxRenderer.Render(parsed.ModelRemains);
    }
}

internal sealed class CodingPlanResponse
{
    [JsonPropertyName("model_remains")]
    public List<ModelRemain> ModelRemains { get; set; } = new();
}

internal sealed class ModelRemain
{
    [JsonPropertyName("model_name")]
    public string? ModelName { get; set; }

    [JsonPropertyName("current_interval_total_count")]
    public long CurrentIntervalTotalCount { get; set; }

    [JsonPropertyName("current_interval_remaining_percent")]
    public double? CurrentIntervalRemainingPercent { get; set; }

    [JsonPropertyName("remains_time")]
    public long RemainsTime { get; set; }

    [JsonPropertyName("current_weekly_total_count")]
    public long CurrentWeeklyTotalCount { get; set; }

    [JsonPropertyName("current_weekly_remaining_percent")]
    public double? CurrentWeeklyRemainingPercent { get; set; }

    [JsonPropertyName("weekly_remains_time")]
    public long WeeklyRemainsTime { get; set; }
}

[JsonSerializable(typeof(CodingPlanResponse))]
internal partial class CodingPlanJsonContext : JsonSerializerContext
{
}