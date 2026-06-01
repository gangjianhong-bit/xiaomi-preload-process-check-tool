namespace VSCode_C_;

/// <summary>
/// Unified interface for all hardware/software health checker modules.
/// Each module implements this to plug into the diagnostic engine.
/// </summary>
public interface IHealthChecker
{
    /// <summary>Unique module name displayed in reports and logs.</summary>
    string ModuleName { get; }

    /// <summary>
    /// Execute the health check and return structured results.
    /// Called by DiagnosticEngine; implementations should not throw.
    /// </summary>
    Task<CheckResult> CheckAsync(IDiagnosticLogger logger, CancellationToken ct);
}

/// <summary>
/// Logging sink for diagnostic output.
/// Provides structured pass / fail / warning / info channels.
/// </summary>
public interface IDiagnosticLogger
{
    void LogSection(string title);
    void LogPass(string module, string checkName, string detail);
    void LogFail(string module, string checkName, string detail);
    void LogWarning(string module, string checkName, string detail);
    void LogInfo(string module, string message);
    void LogRaw(string line);

    /// <summary>
    /// Prompt the operator for a manual check result.
    /// Returns CheckStatus.Pass, .Fail, or .Warning based on operator input.
    /// </summary>
    CheckStatus PromptOperator(string checkName, string question, string? detail = null);
}
