namespace VSCode_C_;

/// <summary>Outcome of a single sub-check.</summary>
public enum CheckStatus
{
    Pass,
    Fail,
    Warning,
    Skipped
}

/// <summary>Result of one atomic check inside a module.</summary>
public sealed record SubCheckResult
{
    public required string Name { get; init; }
    public required CheckStatus Status { get; init; }
    public required string Message { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Aggregated result returned by a health-checker module.</summary>
public sealed record CheckResult
{
    public required string ModuleName { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public List<SubCheckResult> SubChecks { get; init; } = new();

    public int PassCount => SubChecks.Count(s => s.Status == CheckStatus.Pass);
    public int FailCount => SubChecks.Count(s => s.Status == CheckStatus.Fail);
    public int WarningCount => SubChecks.Count(s => s.Status == CheckStatus.Warning);

    public CheckStatus OverallStatus =>
        FailCount > 0 ? CheckStatus.Fail
        : WarningCount > 0 ? CheckStatus.Warning
        : PassCount > 0 ? CheckStatus.Pass
        : CheckStatus.Skipped;
}
