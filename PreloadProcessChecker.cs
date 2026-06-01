using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VSCode_C_;

[SupportedOSPlatform("windows")]
public sealed class PreloadProcessChecker : IHealthChecker
{
    public string ModuleName => "PreloadProcess";
    private readonly string _toolsDir;

    public PreloadProcessChecker(string? toolsDir = null)
    {
        _toolsDir = toolsDir ?? ResolveDefaultToolsPath();
    }

    public static string ResolveDefaultToolsPath()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            var dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                return dir;
        }
        return AppContext.BaseDirectory;
    }

    public async Task<CheckResult> CheckAsync(IDiagnosticLogger logger, CancellationToken ct)
    {
        var result = new CheckResult { ModuleName = ModuleName };
        logger.LogSection("Preload Process Checks");

        result.SubChecks.Add(Check1_PreloadManual(logger, ct));
        result.SubChecks.Add(await Check2_OobePostTimeAsync(logger, ct));
        result.SubChecks.Add(await Check3_OobeProcessAsync(logger, ct));
        result.SubChecks.Add(Check4_Login(logger, ct));
        result.SubChecks.Add(Check5_Desktop(logger, ct));
        result.SubChecks.Add(Check6_RootDriver(logger, ct));
        result.SubChecks.Add(await Check7_SacvtAsync(logger, ct));
        result.SubChecks.Add(await Check8_CsupAsync(logger, ct));
        result.SubChecks.Add(Check9_Markfile(logger, ct));
        result.SubChecks.Add(await Check10_VirusScan(logger, ct));
        result.SubChecks.Add(await Check11_SecureBoot(logger, ct));
        result.SubChecks.Add(Check12_PreloadVersion(logger, ct));
        result.SubChecks.Add(await Check13_VmpAsync(logger, ct));
        result.SubChecks.Add(await Check14_MemoryIntegrityAsync(logger, ct));
        result.SubChecks.Add(Check15_DynamicRefreshRate(logger, ct));

        return result;
    }

    // ── 1. Preload Run Check (Manual — cannot automate screen observation) ──
    private SubCheckResult Check1_PreloadManual(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "1. Preload Run Check";
        logger.LogInfo("PreloadProcess", $"Check {name}: operator confirms no error/quit/hang during preload");
        return LogManual(logger, name,
            "No error popup, no QUIT execution, no system hang/halt, no operator interruptions during preload?",
            "Observe full preload flow.");
    }

    // ── 2. OOBE Post Time — BIOS time (FPDT) + OS boot duration ──
    private async Task<SubCheckResult> Check2_OobePostTimeAsync(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "2. OOBE Post Time";
        logger.LogInfo("PreloadProcess", $"Check {name}: boot duration from System EventLog (Event 12→6005 span)");
        double? bootSec = null;
        var details = new List<string>();

        // Method: System EventLog Event ID 12 (Kernel-General: OS started) → Event ID 6005 (EventLog started).
        // The span between these two events is the OS boot duration — a FIXED value for the current boot session.
        try
        {
            using var log = new EventLog("System");
            var recentEntries = log.Entries.Cast<EventLogEntry>()
                .Where(e => e.InstanceId is 12 or 6005)
                .OrderByDescending(e => e.TimeGenerated)
                .Take(20)
                .OrderBy(e => e.TimeGenerated)
                .ToList();

            // Find the pair: Event 12 (kernel boot start) then Event 6005 (event log started)
            var ev12 = recentEntries.LastOrDefault(e => e.InstanceId == 12 && e.Source.Contains("Kernel", StringComparison.OrdinalIgnoreCase));
            var ev6005 = recentEntries.LastOrDefault(e => e.InstanceId == 6005);

            if (ev12 != null && ev6005 != null && ev6005.TimeGenerated > ev12.TimeGenerated)
            {
                bootSec = (ev6005.TimeGenerated - ev12.TimeGenerated).TotalSeconds;
                logger.LogInfo("PreloadProcess",
                    $"Boot duration: {bootSec:F1}s (Event 12 [{ev12.TimeGenerated:HH:mm:ss}] → Event 6005 [{ev6005.TimeGenerated:HH:mm:ss}])");
                details.Add($"Event 12→6005: {bootSec:F1}s");
            }
            else
            {
                logger.LogInfo("PreloadProcess",
                    $"Events found: 12={(ev12 != null ? ev12.TimeGenerated.ToString("HH:mm:ss") : "none")}, 6005={(ev6005 != null ? ev6005.TimeGenerated.ToString("HH:mm:ss") : "none")}");
            }
        }
        catch (Exception ex)
        {
            logger.LogInfo("PreloadProcess", $"EventLog boot check failed: {ex.Message}");
        }

        // Fallback: if Event ID 12 not found, try WMI LastBootUpTime — but only useful if boot was very recent
        if (bootSec == null)
        {
            try
            {
                var upSec = RunPowerShellBlocking(
                    "[math]::Round((New-TimeSpan -Start (Get-CimInstance Win32_OperatingSystem).LastBootUpTime -End (Get-Date)).TotalSeconds, 1)");
                if (double.TryParse(upSec, out double uptime) && uptime < 600)
                {
                    bootSec = uptime;
                    details.Add($"Uptime proxy: {uptime:F1}s (booted <10min ago)");
                    logger.LogInfo("PreloadProcess", $"Recent boot — uptime proxy: {uptime:F1}s");
                }
                else if (double.TryParse(upSec, out double u2))
                {
                    logger.LogInfo("PreloadProcess", $"Uptime {u2 / 3600:F1}h — cannot proxy boot duration");
                    details.Add($"Uptime {u2 / 3600:F1}h — boot duration N/A");
                }
            }
            catch { }
        }

        bool isSsd = await IsSystemDriveSsdAsync(ct);
        string driveType = isSsd ? "SSD" : "HDD";
        double threshold = isSsd ? 60 : 360;

        if (bootSec == null)
        {
            logger.LogWarning("PreloadProcess", name, "Boot duration unavailable — system may have been running for a long time.");
            return new() { Name = name, Status = CheckStatus.Warning, Message = "Boot duration N/A", Detail = string.Join("; ", details) };
        }

        if (bootSec.Value < threshold)
        {
            logger.LogPass("PreloadProcess", name, $"{driveType} boot: {bootSec:F1}s (threshold <{threshold:F0}s)");
            return new() { Name = name, Status = CheckStatus.Pass, Message = $"{driveType} boot {bootSec:F1}s OK", Detail = string.Join("; ", details) };
        }

        logger.LogFail("PreloadProcess", name, $"{driveType} boot: {bootSec:F1}s exceeds {threshold:F0}s");
        return new() { Name = name, Status = CheckStatus.Fail, Message = $"Boot too slow: {bootSec:F1}s", Detail = string.Join("; ", details) };
    }

    // ── 3. OOBE Process ──
    private async Task<SubCheckResult> Check3_OobeProcessAsync(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "3. OOBE Process";
        logger.LogInfo("PreloadProcess", $"Check {name}: UI language, Cortana, Edition, EULA");
        var details = new List<string>();
        int pass = 0, warn = 0;

        try
        {
            var langOutput = await RunPowerShellAsync(
                "$langs = Get-WinUserLanguageList -ErrorAction SilentlyContinue; if ($langs) { $langs.LanguageTag -join ', ' } else { (Get-Culture).Name }", ct);
            if (!string.IsNullOrWhiteSpace(langOutput))
            {
                pass++;
                logger.LogPass("PreloadProcess", $"{name} — UI Languages", langOutput);
                details.Add($"UI Languages: {langOutput}");
            }
            else { warn++; details.Add("UI Languages: unavailable"); }
        }
        catch { warn++; }

        try
        {
            var langOutput = await RunPowerShellAsync("(Get-Culture).Name", ct);
            var cortanaSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "en-US", "zh-CN", "zh-Hans-CN", "de", "de-DE", "fr", "fr-FR", "ja", "ja-JP", "es", "es-ES", "it", "it-IT", "pt-BR" };
            if (!cortanaSet.Contains(langOutput.Trim()))
            { warn++; logger.LogWarning("PreloadProcess", $"{name} — Cortana", $"No Cortana voice for: {langOutput.Trim()}"); }
            else { pass++; logger.LogPass("PreloadProcess", $"{name} — Cortana", $"Cortana voice supported: {langOutput.Trim()}"); }
        }
        catch { warn++; }

        try
        {
            var edition = RunPowerShellBlocking("(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion' -Name EditionID -ErrorAction SilentlyContinue).EditionID").Trim();
            details.Add($"Edition: {edition}");
            if (edition is "Core" or "CoreSingleLanguage" or "CoreCountrySpecific" or "CoreN" || edition.Contains("Home"))
            { pass++; logger.LogPass("PreloadProcess", $"{name} — Edition", $"Home/Single Language: {edition}"); }
            else { logger.LogInfo("PreloadProcess", $"Edition: {edition} — non-Home, verify OOBE Cortana"); }
        }
        catch { warn++; }

        var manEula = LogManual(logger, $"{name} — EULA Steps", "All steps during EULA and OOBE normal and successful?", "Confirm each OOBE step.");
        var manOem = LogManual(logger, $"{name} — OEM EULA", "OEM EULA displayed normally?", "Confirm OEM EULA page.");

        int totalFail = (manEula.Status == CheckStatus.Fail ? 1 : 0) + (manOem.Status == CheckStatus.Fail ? 1 : 0);
        return totalFail > 0
            ? new() { Name = name, Status = CheckStatus.Fail, Message = $"OOBE: {totalFail} failure(s)", Detail = string.Join("; ", details) }
            : new() { Name = name, Status = warn > 0 ? CheckStatus.Warning : CheckStatus.Pass, Message = "OOBE Process OK", Detail = string.Join("; ", details) };
    }

    // ── 4. Login — auto check EventLog for login errors ──
    private SubCheckResult Check4_Login(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "4. Login";
        logger.LogInfo("PreloadProcess", $"Check {name}: auto-check System EventLog for login errors");
        try
        {
            using var log = new EventLog("System");
            var errors = log.Entries.Cast<EventLogEntry>()
                .Where(e => e.TimeGenerated > DateTime.Now.AddDays(-1))
                .Where(e => e.EntryType == EventLogEntryType.Error && (e.Source.Contains("Winlogon", StringComparison.OrdinalIgnoreCase) || e.Source.Contains("Logon", StringComparison.OrdinalIgnoreCase)))
                .Take(5).ToList();

            if (errors.Count == 0)
            {
                logger.LogPass("PreloadProcess", name, "No login-related errors in System EventLog (last 24h)");
                return new() { Name = name, Status = CheckStatus.Pass, Message = "No login errors in EventLog" };
            }

            foreach (var e in errors)
                logger.LogWarning("PreloadProcess", name, $"[{e.TimeGenerated:HH:mm}] {e.Source}: {Truncate(e.Message, 80)}");

            logger.LogWarning("PreloadProcess", name, $"{errors.Count} login-related error(s) in System EventLog");
            return new() { Name = name, Status = CheckStatus.Warning, Message = $"{errors.Count} login error(s) found", Detail = "" };
        }
        catch (Exception ex)
        {
            logger.LogWarning("PreloadProcess", name, $"Could not check login events: {ex.Message}");
            return new() { Name = name, Status = CheckStatus.Warning, Message = "Login check unavailable", Detail = ex.Message };
        }
    }

    // ── 5. Desktop — auto scan for extraneous files ──
    private SubCheckResult Check5_Desktop(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "5. Desktop";
        logger.LogInfo("PreloadProcess", $"Check {name}: auto-scan Public + current user desktops");
        var issues = new List<string>();
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "desktop.ini" };

        foreach (var userDir in Directory.GetDirectories(@"C:\Users"))
        {
            var desktop = Path.Combine(userDir, "Desktop");
            if (!Directory.Exists(desktop)) continue;
            try
            {
                foreach (var item in Directory.GetFileSystemEntries(desktop))
                {
                    var itemName = Path.GetFileName(item);
                    if (knownFiles.Contains(itemName)) continue;
                    issues.Add($"{Path.GetFileName(userDir)}\\Desktop\\{itemName}");
                }
            }
            catch { /* skip inaccessible */ }
        }

        // Public desktop
        var publicDesktop = @"C:\Users\Public\Desktop";
        if (Directory.Exists(publicDesktop))
        {
            try
            {
                foreach (var item in Directory.GetFileSystemEntries(publicDesktop))
                {
                    if (knownFiles.Contains(Path.GetFileName(item))) continue;
                    issues.Add($"Public\\Desktop\\{Path.GetFileName(item)}");
                }
            }
            catch { }
        }

        logger.LogInfo("PreloadProcess", $"Desktop scan found {issues.Count} non-standard item(s)");
        foreach (var i in issues) logger.LogRaw($"  Desktop item: {i}");

        if (issues.Count == 0)
        {
            logger.LogPass("PreloadProcess", name, "Desktops are clean — no extraneous files or folders");
            return new() { Name = name, Status = CheckStatus.Pass, Message = "Desktop clean" };
        }

        logger.LogWarning("PreloadProcess", name, $"{issues.Count} file(s)/folder(s) on desktops — review");
        return new() { Name = name, Status = CheckStatus.Warning, Message = $"{issues.Count} desktop item(s)", Detail = string.Join("; ", issues.Take(5)) };
    }

    // ── 6. Root Driver ──
    private SubCheckResult Check6_RootDriver(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "6. Root Driver / Drive Layout";
        logger.LogInfo("PreloadProcess", $"Check {name}: scanning C:\\ and D:\\ root");
        var issues = new List<string>();
        var knownFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "Documents and Settings",
          "$Recycle.Bin", "System Volume Information", "Recovery", "Config.Msi", "DRIVERS", "MSOCache",
          "Intel", "PerfLogs", "Perflogs", "Dell", "HP", "Lenovo", "XIAOMI", "Xiaomi" };
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "autoexec.bat", "config.sys", "bootmgr", "BOOTNXT", "pagefile.sys", "swapfile.sys", "hiberfil.sys",
          "DumpStack.log", "DumpStack.log.tmp", "bootTel.dat" };
        var hiddenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Intel", "PerfLogs", "Perflogs" };

        try
        {
            foreach (var entry in new DirectoryInfo(@"C:\").EnumerateFileSystemInfos())
            {
                if (knownFiles.Contains(entry.Name)) continue;
                if (entry is DirectoryInfo && knownFolders.Contains(entry.Name)) continue;
                bool isHidden = (entry.Attributes & FileAttributes.Hidden) != 0;
                bool isSystem = (entry.Attributes & FileAttributes.System) != 0;
                if (isHidden || isSystem) continue;
                if (entry is FileInfo f && f.Extension.ToLowerInvariant() is ".sys" or ".dll" or ".inf" or ".cat" or ".drv")
                    issues.Add($"Driver file at C:\\ root: {entry.Name}");
                else if (entry is FileInfo)
                    issues.Add($"File at C:\\ root: {entry.Name}");
                else if (entry is DirectoryInfo)
                    issues.Add($"Folder at C:\\ root: {entry.Name}");
            }
        }
        catch (Exception ex) { logger.LogFail("PreloadProcess", name, $"C:\\ scan error: {ex.Message}"); }

        // D:\ OEM check
        try
        {
            if (Directory.Exists(@"D:\"))
                foreach (var d in Directory.GetDirectories(@"D:\"))
                    if (d.Contains("OEM", StringComparison.OrdinalIgnoreCase) || d.Contains("custom", StringComparison.OrdinalIgnoreCase))
                        issues.Add($"OEM folder on D:\\: {Path.GetFileName(d)}");
        }
        catch { }

        if (issues.Count == 0)
        {
            logger.LogPass("PreloadProcess", name, "Drive root layout is clean");
            return new() { Name = name, Status = CheckStatus.Pass, Message = "Drive root clean" };
        }
        logger.LogWarning("PreloadProcess", name, $"{issues.Count} issue(s): {string.Join("; ", issues)}");
        return new() { Name = name, Status = CheckStatus.Warning, Message = $"{issues.Count} root issue(s)", Detail = string.Join("; ", issues) };
    }

    // ── 7. SACVT + DCVT (no timeout) ──
    private async Task<SubCheckResult> Check7_SacvtAsync(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "7. SACVT / DCVT";
        logger.LogInfo("PreloadProcess", $"Check {name}: running SACVT.exe → DCVT.exe (no timeout)");
        var results = new List<string>();

        logger.LogInfo("PreloadProcess", $"Searching for tools in: {_toolsDir}");
        var sacvtPath = FindTool("SACVT.exe");
        logger.LogInfo("PreloadProcess", $"FindTool(SACVT.exe) => {(sacvtPath ?? "NOT FOUND")}");
        if (sacvtPath != null)
        {
            logger.LogInfo("PreloadProcess", $"Running SACVT.exe...");
            var (exitCode, output) = await RunToolNoTimeout(sacvtPath, "", ct);
            logger.LogRaw(output.Length > 500 ? output[..500] : output);
            results.Add($"SACVT.exe: exit {exitCode}");
        }
        else { logger.LogWarning("PreloadProcess", name, "SACVT.exe not found"); results.Add("SACVT.exe not found"); }

        var dcvtPath = FindTool("DCVT.exe");
        if (dcvtPath != null)
        {
            logger.LogInfo("PreloadProcess", $"Running DCVT.exe...");
            var (exitCode, output) = await RunToolNoTimeout(dcvtPath, "", ct);
            logger.LogRaw(output.Length > 500 ? output[..500] : output);
            results.Add($"DCVT.exe: exit {exitCode}");
        }
        else { logger.LogWarning("PreloadProcess", name, "DCVT.exe not found"); results.Add("DCVT.exe not found"); }

        bool missing = results.Any(r => r.Contains("not found"));
        if (missing)
            return new() { Name = name, Status = CheckStatus.Warning, Message = "Tool(s) missing", Detail = string.Join("; ", results) };
        logger.LogPass("PreloadProcess", name, "SACVT/DCVT completed");
        return new() { Name = name, Status = CheckStatus.Pass, Message = "SACVT/DCVT done", Detail = string.Join("; ", results) };
    }

    // ── 8. CSUP + MDAVT (no timeout) ──
    private async Task<SubCheckResult> Check8_CsupAsync(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "8. CSUP";
        logger.LogInfo("PreloadProcess", $"Check {name}: CSUP.TXT + MDAVT.exe (no timeout)");
        var details = new List<string>();
        int fail = 0;

        var csupPath = @"C:\Windows\CSUP.TXT";
        try
        {
            if (File.Exists(csupPath))
            {
                var content = await File.ReadAllTextAsync(csupPath, ct);
                details.Add($"CSUP.TXT: {content.Length}B");
                logger.LogRaw($"CSUP.TXT: {content.Trim()}");
                var m = Regex.Match(content, @"(\d{4}[/-]\d{2}[/-]\d{2})");
                if (m.Success && DateTime.TryParse(m.Groups[1].Value, out var dt))
                {
                    var th = dt.AddMonths(3);
                    if (DateTime.Now > th)
                    { fail++; logger.LogFail("PreloadProcess", $"{name} — GDR", $"CSUP {dt:yyyy-MM-dd} +3mo = {th:yyyy-MM-dd} EXCEEDED — update GDR!"); }
                    else logger.LogPass("PreloadProcess", $"{name} — GDR", $"CSUP {dt:yyyy-MM-dd} +3mo = {th:yyyy-MM-dd} OK");
                }
                else logger.LogWarning("PreloadProcess", $"{name} — GDR", "Cannot parse CSUP date");
            }
            else { logger.LogWarning("PreloadProcess", $"{name} — CSUP.TXT", "Not found"); details.Add("CSUP.TXT not found"); }
        }
        catch (Exception ex) { fail++; logger.LogFail("PreloadProcess", $"{name} — CSUP.TXT", ex.Message); }

        var mdavtPath = FindTool("MDAVT.exe");
        if (mdavtPath != null)
        {
            logger.LogInfo("PreloadProcess", $"Running MDAVT.exe...");
            var (exitCode, output) = await RunToolNoTimeout(mdavtPath, "", ct);
            logger.LogRaw(output.Length > 500 ? output[..500] : output);
            details.Add($"MDAVT.exe: exit {exitCode}");
        }
        else { logger.LogWarning("PreloadProcess", $"{name} — MDAVT", "Not found"); details.Add("MDAVT.exe not found"); }

        if (fail > 0)
            return new() { Name = name, Status = CheckStatus.Fail, Message = $"CSUP: {fail} failure(s)", Detail = string.Join("; ", details) };
        logger.LogPass("PreloadProcess", name, "CSUP OK");
        return new() { Name = name, Status = CheckStatus.Pass, Message = "CSUP OK", Detail = string.Join("; ", details) };
    }

    // ── 9. Markfile ──
    private SubCheckResult Check9_Markfile(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "9. Markfile";
        logger.LogInfo("PreloadProcess", $"Check {name}: scanning *.mrk in Drivers");
        try
        {
            var files = Directory.GetFiles(@"C:\Windows\System32\Drivers", "*.mrk");
            if (files.Length == 0)
            { logger.LogPass("PreloadProcess", name, "No markfiles"); return new() { Name = name, Status = CheckStatus.Pass, Message = "No markfiles" }; }
            var names = files.Select(Path.GetFileName).ToList();
            logger.LogPass("PreloadProcess", name, $"{files.Length} markfile(s): {string.Join(", ", names)}");
            return new() { Name = name, Status = CheckStatus.Pass, Message = $"{files.Length} markfile(s)", Detail = string.Join(", ", names) };
        }
        catch (Exception ex) { logger.LogFail("PreloadProcess", name, ex.Message); return new() { Name = name, Status = CheckStatus.Fail, Message = "Error", Detail = ex.Message }; }
    }

    // ── 10. Virus Scan — auto quick scan ──
    private async Task<SubCheckResult> Check10_VirusScan(IDiagnosticLogger logger, CancellationToken ct)
    {
        // ... (keep the existing implementation from above)
        // This is a placeholder to indicate where Check10 goes — full implementation in the file
        const string name = "10. Virus Scan";
        logger.LogInfo("PreloadProcess", $"Check {name}: Defender status + quick scan");
        var details = new List<string>();
        bool avActive = false, noThreats = true, recentScan = false, scanCompleted = false;

        try
        {
            var defJson = RunPowerShellBlocking("Get-MpComputerStatus | Select-Object AntivirusEnabled,RealTimeProtectionEnabled,LastQuickScanStartTime,LastFullScanStartTime | ConvertTo-Json -Compress");
            logger.LogRaw(defJson.Length > 400 ? defJson[..400] : defJson);
            if (defJson.Contains("\"AntivirusEnabled\":true", StringComparison.OrdinalIgnoreCase)) { avActive = true; details.Add("Defender: enabled"); logger.LogPass("PreloadProcess", $"{name} — Defender", "Active"); }
            var qm = Regex.Match(defJson, @"""LastQuickScanStartTime"":""([^""]*)""");
            var fm = Regex.Match(defJson, @"""LastFullScanStartTime"":""([^""]*)""");
            if (qm.Success && !string.IsNullOrEmpty(qm.Groups[1].Value) && DateTime.TryParse(qm.Groups[1].Value, out var qd) && (DateTime.Now - qd).TotalDays < 7) recentScan = true;
            if (!recentScan && fm.Success && !string.IsNullOrEmpty(fm.Groups[1].Value) && DateTime.TryParse(fm.Groups[1].Value, out var fd) && (DateTime.Now - fd).TotalDays < 30) recentScan = true;
            if (!recentScan) { logger.LogWarning("PreloadProcess", $"{name} — History", "No recent scan on record"); details.Add("No recent scan"); }
        }
        catch (Exception ex) { logger.LogWarning("PreloadProcess", $"{name} — Status", ex.Message); }

        try
        {
            var threats = RunPowerShellBlocking("Get-MpThreat -ErrorAction SilentlyContinue | Select-Object Name,ThreatStatus | ConvertTo-Json -Compress");
            if (threats.Contains("\"Name\""))
            {
                logger.LogRaw($"Threats: {(threats.Length > 400 ? threats[..400] : threats)}");
                int activeT = Regex.Matches(threats, @"""ThreatStatus"":\s*1\b").Count;
                if (activeT > 0) { noThreats = false; logger.LogFail("PreloadProcess", $"{name} — Threats", $"{activeT} active"); details.Add($"{activeT} active threat(s)"); }
                else { logger.LogPass("PreloadProcess", $"{name} — Threats", "None active"); details.Add("No active threats"); }
            }
            else { logger.LogPass("PreloadProcess", $"{name} — Threats", "Clean"); details.Add("Clean"); }
        }
        catch { }

        try
        {
            logger.LogInfo("PreloadProcess", "Running Defender Quick Scan...");
            var (sc, so) = await RunToolNoTimeout("powershell.exe", "-NoProfile -NonInteractive -Command \"Start-MpScan -ScanType QuickScan -ErrorAction Stop\"", ct);
            logger.LogRaw($"QuickScan exit={sc}; {(so.Length > 200 ? so[..200] : so)}");
            if (sc == 0)
            {
                scanCompleted = true;
                var post = RunPowerShellBlocking("Get-MpThreat -ErrorAction SilentlyContinue | ConvertTo-Json -Compress");
                if (post.Contains("\"Name\""))
                {
                    int at = Regex.Matches(post, @"""ThreatStatus"":\s*1\b").Count;
                    if (at > 0) { noThreats = false; logger.LogFail("PreloadProcess", $"{name} — Scan", $"{at} threat(s) found"); details.Add($"Scan: {at} threat(s)"); }
                    else { logger.LogPass("PreloadProcess", $"{name} — Scan", "Clean"); details.Add("Scan: clean"); }
                }
                else { logger.LogPass("PreloadProcess", $"{name} — Scan", "Clean"); details.Add("Scan: clean"); }
            }
            else { logger.LogWarning("PreloadProcess", $"{name} — Scan", $"Exit {sc} (may need Admin)"); details.Add($"Scan failed (exit {sc})"); }
        }
        catch (Exception ex) { logger.LogWarning("PreloadProcess", $"{name} — Scan", ex.Message); details.Add("Scan error"); }

        if (!avActive) return new() { Name = name, Status = CheckStatus.Fail, Message = "No AV active", Detail = string.Join("; ", details) };
        if (!noThreats) return new() { Name = name, Status = CheckStatus.Fail, Message = "Threats detected", Detail = string.Join("; ", details) };
        if (!scanCompleted && !recentScan) return new() { Name = name, Status = CheckStatus.Warning, Message = "Scan pending (Admin needed)", Detail = string.Join("; ", details) };
        logger.LogPass("PreloadProcess", name, "Virus scan clean");
        return new() { Name = name, Status = CheckStatus.Pass, Message = "Clean", Detail = string.Join("; ", details) };
    }

    // ── 11. Secure Boot + PXE ──
    private async Task<SubCheckResult> Check11_SecureBoot(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "11. Secure Boot + PXE";
        logger.LogInfo("PreloadProcess", $"Check {name}: Confirm-SecureBootUEFI + bcdedit");
        var details = new List<string>();
        bool sbOk = false, pxeOk = true;

        try
        {
            var r = RunPowerShellBlocking("Confirm-SecureBootUEFI -ErrorAction SilentlyContinue");
            logger.LogRaw($"Confirm-SecureBootUEFI: '{r}'");
            if (r.Contains("True")) { sbOk = true; logger.LogPass("PreloadProcess", $"{name} — Secure Boot", "ON"); details.Add("Secure Boot: ON"); }
            else if (r.Contains("False")) { logger.LogFail("PreloadProcess", $"{name} — Secure Boot", "OFF"); details.Add("Secure Boot: OFF"); }
            else { sbOk = CheckSecureBootReg(details); logger.LogInfo("PreloadProcess", $"Fallback registry: sbOk={sbOk}"); }
        }
        catch { sbOk = CheckSecureBootReg(details); }

        try
        {
            var (_, pxeOut) = await RunToolAsync("bcdedit.exe", "/enum all", 15_000, ct);
            bool pxe = pxeOut.Contains("pxeboot", StringComparison.OrdinalIgnoreCase)
                    || pxeOut.Contains("wdsmgfw", StringComparison.OrdinalIgnoreCase)
                    || pxeOut.Contains("wdsnbp", StringComparison.OrdinalIgnoreCase);
            if (pxe) { pxeOk = false; logger.LogWarning("PreloadProcess", $"{name} — PXE", "PXE entries in BCD"); details.Add("PXE: entries detected"); }
            else { logger.LogPass("PreloadProcess", $"{name} — PXE", "Clean"); details.Add("PXE: clean"); }
        }
        catch { details.Add("PXE: bcdedit unavailable"); }

        if (sbOk && pxeOk) return new() { Name = name, Status = CheckStatus.Pass, Message = "Secure Boot ON, PXE clean", Detail = string.Join("; ", details) };
        if (!sbOk && !pxeOk) return new() { Name = name, Status = CheckStatus.Fail, Message = "Both failed", Detail = string.Join("; ", details) };
        return new() { Name = name, Status = CheckStatus.Warning, Message = "One needs attention", Detail = string.Join("; ", details) };
    }

    private static bool CheckSecureBootReg(List<string> d)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"); var v = k?.GetValue("UEFISecureBootEnabled") as int? ?? 0; d.Add($"Reg: UEFISecureBootEnabled={v}"); return v == 1; }
        catch { d.Add("Reg: unavailable"); return false; }
    }

    // ── 12. Preload Version — auto search ──
    private SubCheckResult Check12_PreloadVersion(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "12. Preload Version";
        logger.LogInfo("PreloadProcess", $"Check {name}: auto-search Preload.log");
        var found = FindPreloadLog(logger);
        if (found == null) return new() { Name = name, Status = CheckStatus.Warning, Message = "Preload.log not found", Detail = "Searched C:\\Windows recursively" };
        try
        {
            var c = File.ReadAllText(found);
            logger.LogRaw($"Preload.log ({c.Length}B):\n{(c.Length > 600 ? c[..600] : c)}");
            var vers = Regex.Matches(c, @"\d+\.\d+\.\d+\.\d+").Select(m => m.Value).Distinct().ToList();
            if (vers.Count > 0) { logger.LogPass("PreloadProcess", name, $"Versions: {string.Join(", ", vers)}"); return new() { Name = name, Status = CheckStatus.Pass, Message = $"{vers.Count} version(s)", Detail = string.Join(", ", vers) }; }
            logger.LogWarning("PreloadProcess", name, "No version pattern found in file");
            return new() { Name = name, Status = CheckStatus.Warning, Message = "No version found", Detail = "" };
        }
        catch (Exception ex) { logger.LogFail("PreloadProcess", name, ex.Message); return new() { Name = name, Status = CheckStatus.Fail, Message = "Read error", Detail = ex.Message }; }
    }

    private static string? FindPreloadLog(IDiagnosticLogger logger)
    {
        var known = new[] { @"C:\Windows\Preload.log", @"C:\Windows\Logs\Preload.log", @"C:\Windows\Panther\Preload.log", @"C:\Windows\System32\Preload.log", @"C:\Windows\SysWOW64\Preload.log", @"C:\Windows\System32\sysprep\Preload.log", @"C:\Windows\Setup\Preload.log" };
        foreach (var p in known) if (File.Exists(p)) { logger.LogInfo("PreloadProcess", $"Found: {p}"); return p; }
        try
        {
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WinSxS", "assembly", "Microsoft.NET", "SoftwareDistribution", "System32", "SysWOW64", "servicing", "Prefetch", "Temp", "Logs" };
            foreach (var d in Directory.GetDirectories(@"C:\Windows"))
            {
                if (skip.Contains(Path.GetFileName(d))) continue;
                try
                {
                    var m = Directory.GetFiles(d, "Preload.log", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (m != null) { logger.LogInfo("PreloadProcess", $"Found: {m}"); return m; }
                    foreach (var sd in Directory.GetDirectories(d))
                    {
                        if (skip.Contains(Path.GetFileName(sd))) continue;
                        try { m = Directory.GetFiles(sd, "Preload.log", SearchOption.TopDirectoryOnly).FirstOrDefault(); if (m != null) { logger.LogInfo("PreloadProcess", $"Found: {m}"); return m; } }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ── 13. VMP ──
    private async Task<SubCheckResult> Check13_VmpAsync(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "13. Virtual Machine Platform";
        logger.LogInfo("PreloadProcess", $"Check {name}: dism + locale check");
        try
        {
            var (ex, out_) = await RunToolAsync("dism.exe", "/online /get-features /format:table", 60_000, ct);
            if (ex == 740) return new() { Name = name, Status = CheckStatus.Warning, Message = "dism requires Admin", Detail = "Exit 740" };
            if (ex != 0) return new() { Name = name, Status = CheckStatus.Fail, Message = $"dism exit {ex}" };
            var line = out_.Split('\n').FirstOrDefault(l => l.Contains("VirtualMachinePlatform", StringComparison.OrdinalIgnoreCase));
            logger.LogRaw($"dism VMP: {line?.Trim() ?? "not found"}");
            bool enabled = line?.Contains("Enabled") == true && !line.Contains("Disabled");
            var locale = (await RunPowerShellAsync("(Get-Culture).Name", ct)).Trim();
            bool zhKo = locale.StartsWith("zh-", StringComparison.OrdinalIgnoreCase) || locale.StartsWith("ko-", StringComparison.OrdinalIgnoreCase);
            if (zhKo && !enabled) return new() { Name = name, Status = CheckStatus.Pass, Message = $"VMP Disabled (correct for {locale})" };
            if (!zhKo && enabled) return new() { Name = name, Status = CheckStatus.Pass, Message = "VMP Enabled (correct)" };
            return new() { Name = name, Status = CheckStatus.Fail, Message = $"VMP {(enabled ? "Enabled" : "Disabled")} — wrong for {locale}" };
        }
        catch (Exception ex2) { return new() { Name = name, Status = CheckStatus.Fail, Message = "VMP error", Detail = ex2.Message }; }
    }

    // ── 14. Memory Integrity ──
    private async Task<SubCheckResult> Check14_MemoryIntegrityAsync(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "14. Memory Integrity";
        logger.LogInfo("PreloadProcess", $"Check {name}: HVCI registry + script");
        var d = new List<string>();
        bool hvciOk = false;

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            if (k != null)
            {
                int v = k.GetValue("Enabled") as int? ?? 0;
                d.Add($"HVCI reg: {v}");
                if (v == 1) { hvciOk = true; logger.LogPass("PreloadProcess", $"{name} — Reg", "Enabled"); }
                else
                {
                    var loc = RunPowerShellBlocking("(Get-Culture).Name").Trim();
                    if (loc.StartsWith("zh-") || loc.StartsWith("ko-"))
                    { logger.LogInfo("PreloadProcess", $"{name} — {loc} excluded from auto-enable"); d.Add($"{loc}: excluded"); }
                    else logger.LogWarning("PreloadProcess", $"{name} — Reg", $"Not enabled ({v})");
                }
            }
            else logger.LogWarning("PreloadProcess", $"{name} — Reg", "Key not found");
        }
        catch (Exception ex) { logger.LogFail("PreloadProcess", $"{name} — Reg", ex.Message); }

        var scripts = new[] { "win11-L2-check.bat", "check.cmd", "MemoryIntegrityoncheck-v1.1\\win11-L2-check.bat" };
        foreach (var s in scripts)
        {
            try
            {
                var (w, _) = await RunToolAsync("where.exe", s, 5_000, ct);
                if (w != 0) continue;
                var (sc, so) = await RunToolAsync("cmd.exe", $"/c \"{s}\"", 60_000, ct);
                logger.LogRaw(so.Length > 400 ? so[..400] : so);
                d.Add($"Script {s}: exit {sc}");
                break;
            }
            catch { }
        }

        return hvciOk
            ? new() { Name = name, Status = CheckStatus.Pass, Message = "HVCI enabled", Detail = string.Join("; ", d) }
            : new() { Name = name, Status = CheckStatus.Warning, Message = "HVCI not enabled", Detail = string.Join("; ", d) };
    }

    // ── 15. DRR ──
    private SubCheckResult Check15_DynamicRefreshRate(IDiagnosticLogger logger, CancellationToken ct)
    {
        const string name = "15. Dynamic Refresh Rate";
        logger.LogInfo("PreloadProcess", $"Check {name}: WDDM + monitor refresh rates");
        var d = new List<string>();
        try
        {
            var gpu = RunPowerShellBlocking("Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentRefreshRate,@{N='WDDM';E={if($_.DriverVersion -match '^(\\d+)\\.'){[int]$matches[1]}}} | ConvertTo-Json -Compress");
            logger.LogRaw(gpu.Length > 300 ? gpu[..300] : gpu);
            var wm = Regex.Match(gpu, @"""WDDM"":\s*""?(\d+)""?");
            if (wm.Success && int.TryParse(wm.Groups[1].Value, out int wv) && wv >= 30)
            {
                d.Add($"WDDM {wv / 10.0:F1}: DRR capable");
                logger.LogPass("PreloadProcess", $"{name} — WDDM", $"{wv / 10.0:F1} capable");
            }
        }
        catch (Exception ex) { logger.LogWarning("PreloadProcess", $"{name} — GPU", ex.Message); }

        try
        {
            var modes = RunPowerShellBlocking("(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorListedSupportedSourceModes -ErrorAction SilentlyContinue).MonitorSourceModes | Select-Object -First 30 -ExpandProperty VerticalRefreshRate | Sort-Object -Unique");
            if (!string.IsNullOrWhiteSpace(modes))
            {
                var rates = modes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
                d.Add($"Refresh rates: {string.Join(", ", rates)}Hz");
                if (rates.Count > 1) logger.LogPass("PreloadProcess", $"{name} — Monitor", $"{rates.Count} rates: DRR supported");
            }
        }
        catch { }

        return new() { Name = name, Status = CheckStatus.Pass, Message = d.Count > 0 ? string.Join("; ", d) : "DRR checked", Detail = string.Join("; ", d) };
    }

    // ═══════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════

    private string? FindTool(string exeName)
    {
        try
        {
            var files = Directory.GetFiles(_toolsDir, exeName, SearchOption.AllDirectories);
            return files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FindTool({exeName}) in {_toolsDir}: {ex.Message}");
            return null;
        }
    }

    private static SubCheckResult LogManual(IDiagnosticLogger logger, string name, string question, string? detail)
    {
        var status = logger.PromptOperator(name, question, detail);
        return new() { Name = name, Status = status, Message = status switch { CheckStatus.Pass => "OK", CheckStatus.Fail => "FAIL", CheckStatus.Warning => "WARN", _ => "SKIP" }, Detail = detail };
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = "powershell.exe", Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new Exception("powershell.exe failed");
        var o = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync(ct);
        return o.Trim();
    }

    private static string RunPowerShellBlocking(string script)
    {
        var psi = new ProcessStartInfo { FileName = "powershell.exe", Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var proc = Process.Start(psi);
        if (proc == null) return "";
        using var p = proc;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        return o.Trim();
    }

    private static async Task<(int, string)> RunToolNoTimeout(string filePath, string arguments, CancellationToken ct)
        => await RunToolAsync(filePath, arguments, 0, ct);

    private static bool? _isRunningAsAdmin;
    private static bool IsRunningAsAdmin()
    {
        if (_isRunningAsAdmin == null)
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            _isRunningAsAdmin = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        return _isRunningAsAdmin.Value;
    }

    private static async Task<(int, string)> RunToolAsync(string filePath, string arguments, int timeoutMs, CancellationToken ct)
    {
        var workDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Environment.CurrentDirectory;
        Process? p = null;
        bool canRead = true;

        // When running as Admin, skip UseShellExecute=false — it conflicts with some tool manifests.
        // Go directly to ShellExecute which handles elevation/security boundaries properly.
        if (!IsRunningAsAdmin())
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = filePath, Arguments = arguments, WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                p = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                // Need elevation — fall through to ShellExecute below
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Other startup error — fall through to ShellExecute
            }
        }

        if (p == null)
        {
            var psi = new ProcessStartInfo { FileName = filePath, Arguments = arguments, WorkingDirectory = workDir, UseShellExecute = true, CreateNoWindow = false };
            if (!IsRunningAsAdmin())
                psi.Verb = "runas"; // request elevation if not already admin
            p = Process.Start(psi);
            canRead = false;
        }

        if (p == null) throw new Exception($"Failed: {filePath}");
        CancellationTokenSource? tcs = timeoutMs > 0 ? new(timeoutMs) : null;
        var token = tcs != null ? CancellationTokenSource.CreateLinkedTokenSource(ct, tcs.Token).Token : ct;
        var outTask = canRead ? Task.Run(async () => { var so = await p.StandardOutput.ReadToEndAsync(); var se = await p.StandardError.ReadToEndAsync(); return (so + "\n" + se).Trim(); }) : Task.FromResult("(output not captured)");
        try { await p.WaitForExitAsync(token); }
        catch (OperationCanceledException) { try { p.Kill(); } catch { } return (-1, timeoutMs > 0 ? $"[Timeout {timeoutMs}ms]" : "[Cancelled]"); }
        finally { tcs?.Dispose(); }
        return (p.ExitCode, await outTask);
    }

    private static async Task<bool> IsSystemDriveSsdAsync(CancellationToken ct)
    {
        try { var r = await RunPowerShellAsync("(Get-PhysicalDisk | Where-Object {$_.DeviceId -eq (Get-Partition -DriveLetter C).DiskNumber}).MediaType", ct); return r.Contains("SSD", StringComparison.OrdinalIgnoreCase); }
        catch { return true; }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}
