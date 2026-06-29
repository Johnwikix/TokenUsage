using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenUsage.Providers.DeepSeek;

internal sealed class DeepSeekProvider : IProvider
{
    public string Name => "deepseek";
    public string DisplayTitle => "DeepSeek 余额";

    private const string EndpointUrl = "https://api.deepseek.com/user/balance";

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
                    "token 无效或已过期，请登录 https://platform.deepseek.com 重置 API Key");
            }
            var preview = body.Length > 200 ? body[..200] : body;
            throw new ProviderException(
                ProviderExceptionKind.ApiError,
                $"API 返回 {(int)response.StatusCode} {response.ReasonPhrase}：{preview}");
        }

        BalanceResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, DeepSeekJsonContext.Default.BalanceResponse);
        }
        catch (JsonException ex)
        {
            throw new ProviderException(ProviderExceptionKind.ApiError, $"响应 JSON 解析失败: {ex.Message}");
        }

        if (parsed is null)
        {
            throw new ProviderException(ProviderExceptionKind.ApiError, "API 响应为空");
        }

        return DeepSeekRenderer.Render(parsed);
    }
}

internal sealed class BalanceResponse
{
    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("balance_infos")]
    public List<BalanceInfo> BalanceInfos { get; set; } = new();
}

internal sealed class BalanceInfo
{
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("total_balance")]
    public string TotalBalance { get; set; } = "0";

    [JsonPropertyName("granted_balance")]
    public string GrantedBalance { get; set; } = "0";

    [JsonPropertyName("topped_up_balance")]
    public string ToppedUpBalance { get; set; } = "0";
}

[JsonSerializable(typeof(BalanceResponse))]
internal partial class DeepSeekJsonContext : JsonSerializerContext
{
}