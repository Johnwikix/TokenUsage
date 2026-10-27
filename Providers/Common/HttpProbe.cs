using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenUsage.Providers.Common;

internal sealed record Endpoint(string Url, bool Bearer = true);

internal sealed record ProbeResponse(int Status, string Body);

internal static class HttpProbe
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // 每个请求用独立 CTS 控制超时，便于按 provider 设置不同超时
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static async Task<(JsonDocument Doc, string Url)> GetJsonAnyAsync(
        IReadOnlyList<Endpoint> endpoints,
        string token,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        string authHint,
        CancellationToken cancellationToken)
    {
        ProbeResponse? denied = null;
        ProviderException? lastError = null;

        foreach (var endpoint in endpoints)
        {
            ProbeResponse response;
            try
            {
                response = await GetAsync(endpoint, token, headers, timeout, cancellationToken);
            }
            catch (ProviderException ex) when (ex.Kind == ProviderExceptionKind.Network)
            {
                lastError = ex;
                continue;
            }

            if (response.Status is 401 or 403)
            {
                denied = response;
                continue;
            }

            if (response.Status is < 200 or >= 300)
            {
                throw new ProviderException(
                    ProviderExceptionKind.ApiError,
                    $"API 返回 HTTP {response.Status}: {Preview(response.Body)}");
            }

            try
            {
                return (JsonDocument.Parse(response.Body), endpoint.Url);
            }
            catch (JsonException ex)
            {
                lastError = new ProviderException(
                    ProviderExceptionKind.ApiError,
                    $"响应 JSON 解析失败: {ex.Message}");
            }
        }

        if (denied is not null)
        {
            throw new ProviderException(
                ProviderExceptionKind.Unauthorized,
                $"{authHint}（HTTP {denied.Status}）");
        }

        throw lastError ?? new ProviderException(ProviderExceptionKind.Network, "所有端点均不可达");
    }

    public static async Task<ProbeResponse> GetAsync(
        Endpoint endpoint,
        string token,
        IReadOnlyDictionary<string, string>? headers,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
        if (endpoint.Bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", token);
        }

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        try
        {
            using var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return new ProbeResponse((int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(
                ProviderExceptionKind.Timeout,
                $"请求超时（{timeout.TotalSeconds:0} 秒）");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(ProviderExceptionKind.Network, $"网络异常: {ex.Message}");
        }
    }

    public static string Preview(string body, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(空响应)";
        var normalized = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }
}
