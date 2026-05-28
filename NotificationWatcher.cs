using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Activity_Tracker
{
    public class NotificationWatcher : BackgroundService
    {
        private readonly ILogger<NotificationWatcher> _logger;
        private readonly LocalStorageService _storageService;
        private readonly TelegramBotService _telegramService;
        private UserNotificationListener? _listener;
        private readonly System.Collections.Generic.HashSet<uint> _processedNotificationIds = new();

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        public NotificationWatcher(
            ILogger<NotificationWatcher> logger,
            LocalStorageService storageService,
            TelegramBotService telegramService)
        {
            _logger = logger;
            _storageService = storageService;
            _telegramService = telegramService;
        }

        private bool IsNotificationListenerSupported()
        {
            try
            {
                return Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener");
            }
            catch
            {
                return false;
            }
        }

        private bool IsActiveConsoleSession()
        {
            try
            {
                uint activeSessionId = WTSGetActiveConsoleSessionId();
                if (activeSessionId == 0xFFFFFFFF) return false;
                uint currentSessionId = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
                return currentSessionId == activeSessionId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to determine if running in active console session");
                return true;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!IsNotificationListenerSupported())
            {
                _logger.LogWarning("⚠️ UserNotificationListener is not supported on this Windows version.");
                return;
            }

            _logger.LogInformation("🔔 Notification Watcher starting...");

            try
            {
                _listener = UserNotificationListener.Current;
                var accessStatus = await _listener.RequestAccessAsync();
                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                {
                    _logger.LogWarning("⚠️ Notification access is not allowed. Please grant Notification Access to this app in Windows Settings.");
                }
                else
                {
                    _listener.NotificationChanged += Listener_NotificationChanged;
                    _logger.LogInformation("🔔 Subscribed to Windows notifications successfully.");
                    
                    // Harvest any existing notifications at startup
                    _ = ProcessLatestNotificationsAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to initialize UserNotificationListener");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        private void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            if (!IsActiveConsoleSession()) return;

            try
            {
                _ = ProcessLatestNotificationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling NotificationChanged event");
            }
        }

        private async Task ProcessLatestNotificationsAsync()
        {
            try
            {
                if (_listener == null) return;
                var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                if (notifications == null || notifications.Count == 0) return;

                foreach (var notif in notifications)
                {
                    uint id = notif.Id;
                    lock (_processedNotificationIds)
                    {
                        if (_processedNotificationIds.Contains(id))
                        {
                            continue;
                        }
                        _processedNotificationIds.Add(id);

                        if (_processedNotificationIds.Count > 1000)
                        {
                            _processedNotificationIds.Clear();
                            _processedNotificationIds.Add(id);
                        }
                    }

                    await ProcessNotificationAsync(notif);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing latest notifications");
            }
        }

        private async Task ProcessNotificationAsync(UserNotification userNotification)
        {
            try
            {
                Notification notification = userNotification.Notification;
                
                string appName = string.Empty;
                try
                {
                    appName = userNotification.AppInfo.DisplayInfo.DisplayName;
                }
                catch {}
                
                if (string.IsNullOrEmpty(appName))
                {
                    appName = "Unknown App";
                }

                string title = string.Empty;
                string message = string.Empty;

                var toastBinding = notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (toastBinding != null)
                {
                    var textElements = toastBinding.GetTextElements();
                    if (textElements.Count > 0)
                    {
                        title = textElements[0].Text ?? string.Empty;
                    }
                    if (textElements.Count > 1)
                    {
                        message = textElements[1].Text ?? string.Empty;
                    }
                }

                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(message))
                {
                    return; 
                }

                var report = new NotificationReport
                {
                    AppName = appName,
                    Title = title,
                    Message = message,
                    Timestamp = DateTime.Now
                };

                _storageService.SaveNotification(report);
                _logger.LogInformation("🔔 Captured Notification from {App}: {Title} - {Message}", appName, title, message);

                string telegramMsg = $"🔔 <b>Notification: [{appName}]</b>\n\n<b>{title}</b>\n{message}";
                await _telegramService.SendSystemNotificationAsync(telegramMsg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing toast notification");
            }
        }
    }
}
