using TokenUsage.Config;

namespace TokenUsage.Providers;

internal interface IProvider
{
    string Name { get; }
    string DisplayTitle { get; }
    Task<string> FetchAndRenderAsync(ProviderConfig config, CancellationToken cancellationToken);

    /// <summary>
    /// 从本机已有的登录态补全凭证（例如 Codex CLI 的 auth.json）。
    /// 默认什么都不做；config.json 里显式配置的 token 优先。
    /// </summary>
    void ApplyAmbientCredentials(ProviderConfig config)
    {
    }
}

internal enum ProviderExceptionKind
{
    Unauthorized,
    ApiError,
    Timeout,
    Network
}

internal sealed class ProviderException : Exception
{
    public ProviderExceptionKind Kind { get; }

    public ProviderException(ProviderExceptionKind kind, string message) : base(message)
    {
        Kind = kind;
    }
}
