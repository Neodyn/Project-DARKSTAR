using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Darkstar;

public enum WindowsServiceState
{
    /// <summary>No service with that name exists.</summary>
    NotInstalled,
    Stopped,
    Running,
    /// <summary>Transitional (StartPending/StopPending) or anything else Windows reported.</summary>
    Other,
    /// <summary>The status could not be determined (PowerShell missing, not running on Windows, ...).</summary>
    Unknown
}

public sealed class WindowsServiceStatus
{
    public WindowsServiceState State { get; init; }
    public string StateText { get; init; } = "";
    /// <summary>"Automatic", "Manual", "Disabled" - empty if unknown.</summary>
    public string StartType { get; init; } = "";
    /// <summary>The executable the service is actually registered to run - the quickest way to spot a stale installation pointing at an old folder.</summary>
    public string BinaryPath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsInstalled => State != WindowsServiceState.NotInstalled && State != WindowsServiceState.Unknown;
}

public sealed class ServiceOperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    /// <summary>Raw output of the elevated commands, for the details box in the GUI.</summary>
    public string Output { get; init; } = "";
}

/// <summary>
/// Installs, removes and controls the bot's Windows Service.
///
/// Everything goes through PowerShell rather than parsing sc.exe's console output: sc.exe
/// localizes its field labels ("STATE" is "ZUSTAND" on a German Windows), so screen-scraping it
/// breaks on any non-English system, while PowerShell's Get-Service returns culture-independent
/// values. Creating the service also uses New-Service instead of sc.exe's quirky "option= value"
/// syntax; only deletion still uses sc.exe, because Remove-Service doesn't exist in the
/// PowerShell 5.1 that ships with Windows.
///
/// Changing a service (create/delete/start/stop) requires administrator rights, so those
/// operations run in a temporary script launched elevated (UAC prompt). An elevated process
/// started that way cannot have its output piped back directly, so the script writes its own
/// output to a log file which is read afterwards. Reading the status needs no elevation.
/// </summary>
public static class WindowsServiceManager
{
    public const string DefaultServiceName = "Darkstar";

    /// <summary>
    /// Service names this bot may already be registered under, newest convention first. The
    /// installer (installer/Setup.iss) uses "D.A.R.K.S.T.A.R.", so a GUI that only ever looked for
    /// "Darkstar" would report "not installed" on a machine that was set up with the installer.
    /// </summary>
    public static IReadOnlyList<string> KnownServiceNames { get; } = new[] { "Darkstar", "D.A.R.K.S.T.A.R." };

    /// <summary>
    /// Returns the name the bot's service is actually registered under, or null if none of the
    /// known names exist. Used by the GUI to preselect the right name on startup.
    /// </summary>
    public static async Task<string?> FindInstalledServiceNameAsync(CancellationToken token = default)
    {
        foreach (var candidate in KnownServiceNames)
        {
            var status = await QueryAsync(candidate, token);
            if (status.IsInstalled) return candidate;
        }
        return null;
    }
    public const string DefaultDisplayName = "D.A.R.K.S.T.A.R. SRS Bot";
    public const string DefaultDescription = "Voice-controlled radio assistant bot for DCS World via DCS-SimpleRadio-Standalone.";

    /// <summary>How long the uninstall waits for the service to actually stop before deleting it.</summary>
    private const int StopTimeoutSeconds = 20;

    /// <summary>ERROR_CANCELLED - the user dismissed the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>Marker the generated scripts print so success can be told apart from a caught error.</summary>
    private const string SuccessMarker = "DARKSTAR_RESULT=OK";

    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Reads the current state of the service. Needs no administrator rights.</summary>
    public static async Task<WindowsServiceStatus> QueryAsync(string serviceName, CancellationToken token = default)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(serviceName))
            return new WindowsServiceStatus { State = WindowsServiceState.Unknown, StateText = "Not available on this system." };

        var name = serviceName.Trim();

        try
        {
            var result = await RunCapturedAsync("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{EscapeForCmdArgument(BuildQueryCommand(name))}\"", token);

            var output = result.Output;
            if (output.Contains("DARKSTAR_NOTINSTALLED", StringComparison.Ordinal))
                return new WindowsServiceStatus { State = WindowsServiceState.NotInstalled, StateText = "Not installed" };

            var stateText = ExtractField(output, "STATE");
            if (string.IsNullOrWhiteSpace(stateText))
                return new WindowsServiceStatus { State = WindowsServiceState.Unknown, StateText = output.Trim() };

            // Get-Service returns the ServiceControllerStatus enum name, which is English
            // regardless of the Windows display language.
            var state = stateText.Equals("Running", StringComparison.OrdinalIgnoreCase) ? WindowsServiceState.Running
                : stateText.Equals("Stopped", StringComparison.OrdinalIgnoreCase) ? WindowsServiceState.Stopped
                : WindowsServiceState.Other;

            return new WindowsServiceStatus
            {
                State = state,
                StateText = stateText,
                StartType = ExtractField(output, "START"),
                BinaryPath = ExtractField(output, "PATH"),
                DisplayName = ExtractField(output, "DISPLAY")
            };
        }
        catch (Exception ex)
        {
            return new WindowsServiceStatus { State = WindowsServiceState.Unknown, StateText = ex.Message };
        }
    }

    /// <summary>Registers the bot as a Windows Service. Requires administrator rights (UAC prompt).</summary>
    public static async Task<ServiceOperationResult> InstallAsync(string serviceName, string displayName,
        string executablePath, string description, bool automaticStart, bool startAfterInstall,
        CancellationToken token = default)
    {
        if (!IsSupported)
            return Fail("Windows Services are only available on Windows.");

        var name = (serviceName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Fail("Please enter a service name.");

        var exePath = (executablePath ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(exePath))
            return Fail("Please enter the path to the bot executable.");
        if (!File.Exists(exePath))
            return Fail($"Executable not found: {exePath}");

        var existing = await QueryAsync(name, token);
        if (existing.IsInstalled)
            return Fail($"A service named \"{name}\" already exists (currently {existing.StateText}). Remove it first, or use a different name.");

        var run = await RunElevatedScriptAsync(
            BuildInstallScript(name, displayName, exePath, description, automaticStart, startAfterInstall), token);
        if (!run.Success)
            return run;

        var status = await QueryAsync(name, token);
        if (!status.IsInstalled)
            return new ServiceOperationResult
            {
                Success = false,
                Message = "The commands ran, but the service is not registered afterwards. See the details below.",
                Output = run.Output
            };

        var startNote = startAfterInstall
            ? status.State == WindowsServiceState.Running
                ? " and started"
                : " (but it is not running yet - see the details below)"
            : "";

        return new ServiceOperationResult
        {
            Success = true,
            Message = $"Service \"{name}\" installed{startNote}.",
            Output = run.Output
        };
    }

    /// <summary>
    /// Stops the service if it's running, waits for it to actually reach "Stopped", and only then
    /// deletes it. Requires administrator rights (UAC prompt).
    /// </summary>
    public static async Task<ServiceOperationResult> UninstallAsync(string serviceName, CancellationToken token = default)
    {
        if (!IsSupported)
            return Fail("Windows Services are only available on Windows.");

        var name = (serviceName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Fail("Please enter a service name.");

        var status = await QueryAsync(name, token);
        if (!status.IsInstalled)
            return Fail($"No service named \"{name}\" is installed.");

        var run = await RunElevatedScriptAsync(BuildUninstallScript(name), token);
        if (!run.Success)
            return run;

        var after = await QueryAsync(name, token);
        if (after.IsInstalled)
        {
            return new ServiceOperationResult
            {
                Success = false,
                Message = $"The service \"{name}\" is still registered. That usually means it is marked for deletion while something still holds a handle on it - " +
                          "close services.msc and the Task Manager's Services tab, then try again (a reboot always clears it).",
                Output = run.Output
            };
        }

        return new ServiceOperationResult { Success = true, Message = $"Service \"{name}\" stopped and removed.", Output = run.Output };
    }

    /// <summary>Starts the installed service. Requires administrator rights (UAC prompt).</summary>
    public static Task<ServiceOperationResult> StartAsync(string serviceName, CancellationToken token = default) =>
        ControlAsync(serviceName, start: true, token);

    /// <summary>Stops the installed service. Requires administrator rights (UAC prompt).</summary>
    public static Task<ServiceOperationResult> StopAsync(string serviceName, CancellationToken token = default) =>
        ControlAsync(serviceName, start: false, token);

    private static async Task<ServiceOperationResult> ControlAsync(string serviceName, bool start, CancellationToken token)
    {
        if (!IsSupported)
            return Fail("Windows Services are only available on Windows.");

        var name = (serviceName ?? "").Trim();
        var status = await QueryAsync(name, token);
        if (!status.IsInstalled)
            return Fail($"No service named \"{name}\" is installed.");

        var run = await RunElevatedScriptAsync(BuildControlScript(name, start), token);
        if (!run.Success)
            return run;

        var after = await QueryAsync(name, token);
        return new ServiceOperationResult
        {
            Success = true,
            Message = $"Service \"{name}\" is now {after.StateText}.",
            Output = run.Output
        };
    }

    // ---------------------------------------------------------------------------------
    // Script building (internal so the exact quoting can be unit-tested)
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// One-liner that prints the service's state in a fixed, parseable and culture-independent
    /// form, or DARKSTAR_NOTINSTALLED when there is no such service.
    /// </summary>
    internal static string BuildQueryCommand(string name)
    {
        var n = PsQuote(name);
        return "$ErrorActionPreference='SilentlyContinue'; " +
               $"$s = Get-Service -Name {n}; " +
               "if (-not $s) { 'DARKSTAR_NOTINSTALLED' } else { " +
               $"$p = (Get-ItemProperty -Path ('HKLM:\\SYSTEM\\CurrentControlSet\\Services\\' + {n})).ImagePath; " +
               "Write-Output ('STATE=' + $s.Status); " +
               "Write-Output ('START=' + $s.StartType); " +
               "Write-Output ('DISPLAY=' + $s.DisplayName); " +
               "Write-Output ('PATH=' + $p) }";
    }

    /// <summary>
    /// Builds the PowerShell commands that register the service. The executable path is passed to
    /// New-Service wrapped in literal quotes, which is what makes a path containing spaces
    /// ("C:\Program Files\...") start correctly once it sits in the service's ImagePath.
    /// </summary>
    internal static string BuildInstallScript(string name, string displayName, string exePath,
        string description, bool automaticStart, bool startAfterInstall)
    {
        var body = new StringBuilder();
        body.AppendLine($"  $params = @{{ Name = {PsQuote(name)}; BinaryPathName = {PsQuote($"\"{exePath}\"")}; StartupType = '{(automaticStart ? "Automatic" : "Manual")}' }}");

        if (!string.IsNullOrWhiteSpace(displayName))
            body.AppendLine($"  $params.DisplayName = {PsQuote(displayName)}");
        if (!string.IsNullOrWhiteSpace(description))
            body.AppendLine($"  $params.Description = {PsQuote(description)}");

        body.AppendLine("  New-Service @params | Out-Null");

        if (automaticStart)
        {
            // Delayed auto-start: on a freshly booted machine, the network and the SRS server are
            // usually not ready yet when ordinary auto-start services come up. New-Service can't
            // express this, so sc.exe sets it afterwards.
            // "start=" and its value are quoted so PowerShell passes them through as two plain
            // arguments instead of trying to interpret the trailing "=" itself - sc.exe expects
            // exactly that split.
            body.AppendLine($"  & sc.exe config {PsQuote(name)} 'start=' 'delayed-auto' | Out-Null");
        }

        if (startAfterInstall)
            body.AppendLine($"  Start-Service -Name {PsQuote(name)}");

        return WrapScript(body.ToString());
    }

    /// <summary>
    /// Builds the PowerShell commands that remove the service cleanly: stop it, wait for it to
    /// actually reach "Stopped", and only then delete it. Deleting a service that is still
    /// running merely marks it for deletion, leaving a ghost entry behind until the next reboot -
    /// exactly the leftover this is meant to avoid.
    /// </summary>
    internal static string BuildUninstallScript(string name)
    {
        var n = PsQuote(name);
        var body = new StringBuilder();

        body.AppendLine($"  $svc = Get-Service -Name {n} -ErrorAction SilentlyContinue");
        body.AppendLine("  if ($svc -and $svc.Status -ne 'Stopped') {");
        body.AppendLine($"    Stop-Service -Name {n} -Force -ErrorAction SilentlyContinue");
        body.AppendLine($"    $deadline = (Get-Date).AddSeconds({StopTimeoutSeconds})");
        body.AppendLine($"    while (((Get-Service -Name {n} -ErrorAction SilentlyContinue).Status -ne 'Stopped') -and ((Get-Date) -lt $deadline)) {{");
        body.AppendLine("      Start-Sleep -Seconds 1");
        body.AppendLine("    }");
        body.AppendLine("  }");
        body.AppendLine($"  & sc.exe delete {n}");
        body.AppendLine("  if ($LASTEXITCODE -ne 0) { throw ('sc.exe delete failed with exit code ' + $LASTEXITCODE) }");

        return WrapScript(body.ToString());
    }

    internal static string BuildControlScript(string name, bool start)
    {
        var n = PsQuote(name);
        var body = start
            ? $"  Start-Service -Name {n}\r\n"
            : $"  Stop-Service -Name {n} -Force\r\n";

        return WrapScript(body);
    }

    /// <summary>
    /// Wraps the commands so that any error is reported as text instead of a silent failure, and
    /// so the script prints the success marker only when everything actually worked.
    /// </summary>
    private static string WrapScript(string body)
    {
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine("try {");
        script.Append(body);
        script.AppendLine($"  Write-Output '{SuccessMarker}'");
        script.AppendLine("} catch {");
        script.AppendLine("  Write-Output ('ERROR: ' + $_.Exception.Message)");
        script.AppendLine("}");
        return script.ToString();
    }

    /// <summary>Single-quoted PowerShell literal - the only escaping needed inside one is doubling the quote.</summary>
    internal static string PsQuote(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

    /// <summary>Escapes a string that is passed inside a double-quoted cmd/process argument.</summary>
    private static string EscapeForCmdArgument(string value) => value.Replace("\"", "\\\"");

    // ---------------------------------------------------------------------------------
    // Process helpers
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Writes the commands to a temporary .ps1 file and runs it elevated. The script redirects its
    /// own output into a log file which is read back afterwards, because a process started
    /// elevated via ShellExecute cannot have its streams redirected by the caller.
    /// </summary>
    private static async Task<ServiceOperationResult> RunElevatedScriptAsync(string script, CancellationToken token)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "darkstar-service");
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.ps1");
        var logPath = Path.ChangeExtension(scriptPath, ".log");

        try
        {
            // Wrapping the whole script in a block whose every stream (*>&1: output, errors,
            // warnings, verbose) is piped into the log file is what lets the caller see what
            // happened inside the elevated process.
            var fullScript = "& {" + Environment.NewLine +
                             script +
                             "} *>&1 | Out-File -FilePath " + PsQuote(logPath) + " -Encoding utf8" + Environment.NewLine;
            await File.WriteAllTextAsync(scriptPath, fullScript, token);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,   // required for Verb = runas
                Verb = "runas",           // triggers the UAC prompt
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process == null)
                return Fail("Could not start the elevated command.");

            await process.WaitForExitAsync(token);

            var output = File.Exists(logPath) ? (await File.ReadAllTextAsync(logPath, token)).Trim() : "";

            if (!output.Contains(SuccessMarker, StringComparison.Ordinal))
            {
                var error = output
                    .Split('\n')
                    .FirstOrDefault(l => l.TrimStart().StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    ?.Trim();

                return new ServiceOperationResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(error)
                        ? $"The command did not complete successfully (exit code {process.ExitCode}). See the details below."
                        : error,
                    Output = output
                };
            }

            return new ServiceOperationResult { Success = true, Message = "", Output = output };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return Fail("Cancelled - administrator rights were not granted (UAC prompt dismissed).");
        }
        catch (Exception ex)
        {
            return Fail($"Unexpected error: {ex.Message}");
        }
        finally
        {
            TryDelete(scriptPath);
            TryDelete(logPath);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCapturedAsync(string fileName, string arguments, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return (-1, "Could not start " + fileName);

        var stdOut = await process.StandardOutput.ReadToEndAsync(token);
        var stdErr = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);

        return (process.ExitCode, (stdOut + stdErr).Trim());
    }

    /// <summary>Reads one "KEY=value" line out of the query output.</summary>
    internal static string ExtractField(string output, string key)
    {
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return trimmed[(key.Length + 1)..].Trim();
        }
        return "";
    }

    private static ServiceOperationResult Fail(string message) => new() { Success = false, Message = message };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file - not worth reporting */ }
    }
}
