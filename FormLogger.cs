namespace VSCode_C_;

/// <summary>
/// WinForms-based diagnostic logger — writes log entries to the main form's output panel.
/// Grid rows are populated from the final CheckResult list, not from individual log calls.
/// </summary>
public sealed class FormLogger : IDiagnosticLogger
{
    private readonly MainForm _form;

    public FormLogger(MainForm form) => _form = form;

    public void LogSection(string title)
        => _form.Invoke(() => _form.LogSection(title));

    public void LogPass(string module, string checkName, string detail)
        => _form.Invoke(() => _form.LogInfo($"[PASS] [{module}] {checkName} — {detail}"));

    public void LogFail(string module, string checkName, string detail)
        => _form.Invoke(() => _form.LogInfo($"[FAIL] [{module}] {checkName} — {detail}"));

    public void LogWarning(string module, string checkName, string detail)
        => _form.Invoke(() => _form.LogInfo($"[WARN] [{module}] {checkName} — {detail}"));

    public void LogInfo(string module, string message)
        => _form.Invoke(() => _form.LogInfo($"[{module}] {message}"));

    public void LogRaw(string line)
        => _form.Invoke(() => _form.LogRaw(line));

    public CheckStatus PromptOperator(string checkName, string question, string? detail = null)
    {
        CheckStatus result = CheckStatus.Skipped;
        _form.Invoke(() =>
        {
            using var dlg = new ManualCheckDialog(checkName, question, detail);
            result = dlg.ShowDialog(_form) == DialogResult.OK
                ? dlg.SelectedStatus
                : CheckStatus.Skipped;
        });
        return result;
    }
}
