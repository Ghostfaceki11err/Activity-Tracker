using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Activity_Tracker;

/// <summary>
/// Installs or removes auto-start via Startup folder, Task Scheduler, and registry Run key.
/// </summary>
internal static class StartupPersistence
{
    public const string TaskName = "ActivityTracker";
    public const string RegistryValueName = "ActivityTracker";
    public const string ShortcutFileName = "Activity Tracker.lnk";
    public static readonly string SilentLaunchArgs = "--no-console --no-persist";

    public static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ActivityTracker", "persistence_installed.marker");

    public static string StartupFolderPath => Environment.GetFolderPath(
        Environment.SpecialFolder.CommonStartup);

    public static bool IsInstalled => File.Exists(MarkerPath);

    public static InstallResult Install(string exePath, string workingDir, bool silent)
    {
        var errors = new List<string>();
        int succeeded = 0;

        // Skip Task Scheduler for multi-user support - it causes session isolation issues
        // Rely on system-wide Startup folder and Registry entries instead
        if (InstallStartupShortcut(exePath, workingDir, out var shortcutError))
            succeeded++;
        else if (shortcutError != null)
            errors.Add(shortcutError);

        if (InstallRegistryRun(exePath, workingDir, out var registryError))
            succeeded++;
        else if (registryError != null)
            errors.Add(registryError);

        if (succeeded > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath,
                $"Installed: {DateTime.Now:O}{Environment.NewLine}Path: {exePath}{Environment.NewLine}Args: {SilentLaunchArgs}");
        }

        if (!silent && succeeded == 2 && errors.Count == 0)
        {
            MessageBox.Show(
                "Activity Tracker will start automatically in the background when you sign in.",
                "Setup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (!silent && errors.Count > 0)
        {
            MessageBox.Show(
                $"Some auto-start entries could not be created:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}",
                "Setup Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return new InstallResult(succeeded, errors);
    }

    public static void Uninstall(bool showMessage)
    {
        var errors = new List<string>();

        // Task Scheduler is no longer used for multi-user support
        // Skip deletion to avoid errors

        try
        {
            var shortcutPath = Path.Combine(StartupFolderPath, ShortcutFileName);
            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);
        }
        catch (Exception ex)
        {
            errors.Add($"Startup folder: {ex.Message}");
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(RegistryValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            errors.Add($"Registry: {ex.Message}");
        }

        if (File.Exists(MarkerPath))
        {
            try { File.Delete(MarkerPath); } catch { }
        }

        if (showMessage)
        {
            var text = errors.Count == 0
                ? "Auto-start has been removed successfully."
                : $"Auto-start removed with warnings:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}";
            MessageBox.Show(text, "Activity Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public static bool LaunchBackgroundInstance(string exePath, string workingDir)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = SilentLaunchArgs,
                WorkingDirectory = workingDir,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool InstallScheduledTask(string exePath, string workingDir, out string? error)
    {
        error = null;
        try
        {
            string escapedExe = exePath.Replace("'", "''");
            string escapedDir = workingDir.Replace("'", "''");
            string escapedArgs = SilentLaunchArgs.Replace("'", "''");

            string psScript = $@"
$action  = New-ScheduledTaskAction -Execute '{escapedExe}' -Argument '{escapedArgs}' -WorkingDirectory '{escapedDir}'
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -Hidden
$principal = New-ScheduledTaskPrincipal -UserId '{Environment.UserName}' -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force
";
            string tempScript = Path.Combine(Path.GetTempPath(), "at_install_task.ps1");
            File.WriteAllText(tempScript, psScript);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            string stdErr = proc?.StandardError.ReadToEnd() ?? "";
            proc?.WaitForExit(15000);
            try { File.Delete(tempScript); } catch { }

            if (proc?.ExitCode != 0)
            {
                error = $"Task Scheduler: {stdErr.Trim()}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Task Scheduler: {ex.Message}";
            return false;
        }
    }

    private static bool InstallStartupShortcut(string exePath, string workingDir, out string? error)
    {
        error = null;
        try
        {
            string shortcutPath = Path.Combine(StartupFolderPath, ShortcutFileName);
            string escapedShortcut = shortcutPath.Replace("'", "''");
            string escapedExe = exePath.Replace("'", "''");
            string escapedDir = workingDir.Replace("'", "''");
            string escapedArgs = SilentLaunchArgs.Replace("'", "''");

            string psScript = $@"
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut('{escapedShortcut}')
$sc.TargetPath = '{escapedExe}'
$sc.Arguments = '{escapedArgs}'
$sc.WorkingDirectory = '{escapedDir}'
$sc.WindowStyle = 7
$sc.Description = 'Activity Tracker'
$sc.Save()
";
            string tempScript = Path.Combine(Path.GetTempPath(), "at_install_startup.ps1");
            File.WriteAllText(tempScript, psScript);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            string stdErr = proc?.StandardError.ReadToEnd() ?? "";
            proc?.WaitForExit(15000);
            try { File.Delete(tempScript); } catch { }

            if (proc?.ExitCode != 0 || !File.Exists(shortcutPath))
            {
                error = $"Startup folder: {stdErr.Trim()}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Startup folder: {ex.Message}";
            return false;
        }
    }

    private static bool InstallRegistryRun(string exePath, string workingDir, out string? error)
    {
        error = null;
        try
        {
            string command = $"\"{exePath}\" {SilentLaunchArgs}";
            using var key = Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null)
            {
                error = "Registry: could not open Run key.";
                return false;
            }

            key.SetValue(RegistryValueName, command);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Registry: {ex.Message}";
            return false;
        }
    }

    internal readonly record struct InstallResult(int SucceededCount, IReadOnlyList<string> Errors);
}
