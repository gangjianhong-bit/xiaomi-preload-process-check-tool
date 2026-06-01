namespace VSCode_C_;

/// <summary>
/// Orchestrates all registered IHealthChecker modules, runs them sequentially,
/// and produces an aggregated summary report.
/// </summary>
public sealed class DiagnosticEngine
{
    private readonly List<IHealthChecker> _modules = new();
    private readonly IDiagnosticLogger _logger;

    public DiagnosticEngine(IDiagnosticLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Register a health-checker module. Order of registration = order of execution.</summary>
    public DiagnosticEngine Register(IHealthChecker module)
    {
        _modules.Add(module);
        return this;
    }

    /// <summary>Register multiple modules at once.</summary>
    public DiagnosticEngine RegisterAll(params IHealthChecker[] modules)
    {
        _modules.AddRange(modules);
        return this;
    }

    /// <summary>
    /// Run all registered modules and return an aggregated diagnostic report.
    /// </summary>
    public async Task<DiagnosticReport> RunAsync(CancellationToken ct = default)
    {
        _logger.LogSection("System Health Diagnostic — Starting");
        _logger.LogInfo("Engine", $"Registered modules: {_modules.Count}");
        foreach (var m in _modules)
            _logger.LogInfo("Engine", $"  - {m.ModuleName}");

        var report = new DiagnosticReport
        {
            StartTime = DateTime.Now,
            ModuleCount = _modules.Count
        };

        foreach (var module in _modules)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await module.CheckAsync(_logger, ct);
                report.Results.Add(result);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Engine", module.ModuleName, "Cancelled by user");
                report.Results.Add(new CheckResult
                {
                    ModuleName = module.ModuleName,
                    SubChecks = { new() { Name = "Execution", Status = CheckStatus.Skipped, Message = "Cancelled" } }
                });
            }
            catch (Exception ex)
            {
                _logger.LogFail("Engine", module.ModuleName, $"Module threw unhandled exception: {ex.Message}");
                report.Results.Add(new CheckResult
                {
                    ModuleName = module.ModuleName,
                    SubChecks = { new() { Name = "Execution", Status = CheckStatus.Fail, Message = "Module crashed", Detail = ex.Message } }
                });
            }
        }

        report.EndTime = DateTime.Now;
        PrintSummary(report);
        return report;
    }

    private void PrintSummary(DiagnosticReport report)
    {
        _logger.LogSection("Summary Report");

        var allSubChecks = report.Results.SelectMany(r => r.SubChecks).ToList();
        int totalPass = allSubChecks.Count(s => s.Status == CheckStatus.Pass);
        int totalFail = allSubChecks.Count(s => s.Status == CheckStatus.Fail);
        int totalWarn = allSubChecks.Count(s => s.Status == CheckStatus.Warning);
        int totalSkip = allSubChecks.Count(s => s.Status == CheckStatus.Skipped);

        Console.WriteLine();
        Console.WriteLine($"  Total modules  : {report.ModuleCount}");
        Console.WriteLine($"  Total checks   : {allSubChecks.Count}");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Pass           : {totalPass}");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  Fail           : {totalFail}");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  Warning        : {totalWarn}");
        Console.ResetColor();
        Console.WriteLine($"  Skipped        : {totalSkip}");
        Console.WriteLine($"  Duration       : {(report.EndTime - report.StartTime).TotalSeconds:F1}s");

        Console.WriteLine();
        if (totalFail > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  OVERALL: FAIL — {totalFail} check(s) failed");
            Console.ResetColor();
        }
        else if (totalWarn > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  OVERALL: WARNING — {totalWarn} check(s) reported warnings");
            Console.ResetColor();
        }
        else if (totalPass > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  OVERALL: PASS — all {totalPass} checks passed");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine("  OVERALL: NO DATA — no checks were executed");
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
    }
}

/// <summary>Aggregated report from a full diagnostic run.</summary>
public sealed record DiagnosticReport
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int ModuleCount { get; set; }
    public List<CheckResult> Results { get; init; } = new();
}
