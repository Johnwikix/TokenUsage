using TokenUsage.Providers.Balance;
using TokenUsage.Providers.Codex;
using TokenUsage.Providers.Glm;
using TokenUsage.Providers.Kimi;
using TokenUsage.Providers.Minimax;
using TokenUsage.Providers.OpenCode;

namespace TokenUsage.Providers;

internal static class ProviderRegistry
{
    private static readonly IProvider[] All = Build();
    private static readonly Dictionary<string, IProvider> ByName =
        All.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    private static IProvider[] Build()
    {
        var providers = new List<IProvider>();
        providers.AddRange(BalanceProviders.CreateAll());
        providers.Add(new KimiProvider());
        providers.Add(new GlmProvider());
        providers.Add(new MinimaxProvider());
        providers.Add(new OpenCodeProvider());
        providers.Add(new CodexProvider());
        return providers.ToArray();
    }

    public static IProvider? TryGet(string name) =>
        ByName.TryGetValue(name, out var provider) ? provider : null;

    public static IReadOnlyList<string> KnownNames() =>
        All.Select(provider => provider.Name).ToArray();

    public static IReadOnlyList<IProvider> AllProviders => All;
}
