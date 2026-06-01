namespace VSCode_C_;

/// <summary>
/// Console-based diagnostic logger with color-coded pass/fail/warning output.
/// </summary>
public sealed class ConsoleLogger : IDiagnosticLogger
{
    public void LogSection(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine();
    }

    public void LogPass(string module, string checkName, string detail)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  [PASS] ");
        Console.ResetColor();
        Console.WriteLine($"[{module}] {checkName} — {detail}");
    }

    public void LogFail(string module, string checkName, string detail)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  [FAIL] ");
        Console.ResetColor();
        Console.WriteLine($"[{module}] {checkName} — {detail}");
    }

    public void LogWarning(string module, string checkName, string detail)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  [WARN] ");
        Console.ResetColor();
        Console.WriteLine($"[{module}] {checkName} — {detail}");
    }

    public void LogInfo(string module, string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("  [INFO] ");
        Console.ResetColor();
        Console.WriteLine($"[{module}] {message}");
    }

    public void LogRaw(string line)
    {
        Console.WriteLine($"         {line}");
    }

    public CheckStatus PromptOperator(string checkName, string question, string? detail = null)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"  ┌─ MANUAL CHECK ────────────────────────────────────────────");
        Console.WriteLine($"  │  {checkName}");
        Console.WriteLine($"  │  {question}");
        if (!string.IsNullOrEmpty(detail))
            Console.WriteLine($"  │  {detail}");
        Console.WriteLine($"  └──────────────────────────────────────────────────────────");
        Console.ResetColor();

        // If stdin is redirected (CI/pipeline), auto-skip all manual checks
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("  [AUTO-SKIP] Non-interactive environment — manual check skipped.");
            return CheckStatus.Skipped;
        }

        while (true)
        {
            Console.Write("  >>> Enter result ([P]ass / [F]ail / [W]arn / [S]kip): ");
            var line = Console.ReadLine()?.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(line))
                continue;

            if (line == "P" || line.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
                return CheckStatus.Pass;
            if (line == "F" || line.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase))
                return CheckStatus.Fail;
            if (line == "W" || line.StartsWith("WARN", StringComparison.OrdinalIgnoreCase))
                return CheckStatus.Warning;
            if (line == "S" || line.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase))
                return CheckStatus.Skipped;

            Console.WriteLine("  [!] Invalid input. Type P, F, W, or S.");
        }
    }
}
