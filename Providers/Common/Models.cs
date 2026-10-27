namespace TokenUsage.Providers.Common;

internal sealed class BalanceLine
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

internal sealed class BalanceReport
{
    public string Currency { get; init; } = string.Empty;
    public string Available { get; init; } = "0";
    public List<BalanceLine> Lines { get; init; } = new();
    public string? Note { get; init; }
}

internal sealed class PlanWindow
{
    public string Label { get; init; } = string.Empty;
    public double? Percent { get; init; }
    public double? Used { get; init; }
    public double? Limit { get; init; }
    public long ResetMs { get; init; }
    public bool IsDetail { get; init; }
    public string? Note { get; init; }
}

internal sealed class PlanReport
{
    public string? PlanName { get; init; }
    public List<PlanWindow> Windows { get; init; } = new();
    public string? Note { get; init; }
}
