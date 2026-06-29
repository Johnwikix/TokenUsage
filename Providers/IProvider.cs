using TokenUsage.Errors;

namespace TokenUsage.Providers;

internal interface IProvider
{
    string Name { get; }
    string DisplayTitle { get; }
    Task<string> FetchAndRenderAsync(string token, CancellationToken cancellationToken);
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

internal static class ProviderRegistry
{
    private static readonly IProvider[] All = new IProvider[]
    {
        new Minimax.MinimaxProvider(),
        new DeepSeek.DeepSeekProvider(),
    };

    private static readonly Dictionary<string, IProvider> ByName =
        All.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public static IProvider? TryGet(string name) =>
        ByName.TryGetValue(name, out var p) ? p : null;

    public static IReadOnlyList<string> KnownNames() =>
        All.Select(p => p.Name).ToArray();
}