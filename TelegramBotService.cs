using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Activity_Tracker
{
    /// <summary>
    /// Service for sending activity reports to Telegram with robust error handling
    /// </summary>
    public class TelegramBotService
    {
        #region Fields

        private readonly ILogger<TelegramBotService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _botToken;
        private readonly string _chatId;
        private readonly bool _enablePrivacyMode;
        private readonly int _maxMessageLength;
        private readonly TimeSpan _timeout;

        // Privacy configurations
        private readonly bool _hashWindowTitles;
        private readonly bool _removeSensitiveKeywords;
        private readonly List<string> _sensitiveKeywords;
        private readonly string _sensitiveReplacementText;

        private const string TelegramApiBaseUrl = "https://api.telegram.org/bot";

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the TelegramBotService
        /// </summary>
        /// <param name="config">Configuration instance</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="httpClient">HttpClient instance (injected via HttpClientFactory)</param>
        public TelegramBotService(
            IConfiguration config,
            ILogger<TelegramBotService> logger,
            HttpClient httpClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            // Validate and load configuration
            ValidateConfiguration(config);

            _botToken = config["Telegram:BotToken"]!;
            _chatId = config["Telegram:ChatId"]!;

            // Load optional configuration with defaults
            _enablePrivacyMode = config.GetValue("Telegram:EnablePrivacyMode", false);
            _maxMessageLength = config.GetValue("Telegram:MaxMessageLength", 1000);
            _timeout = TimeSpan.FromSeconds(config.GetValue("Telegram:TimeoutSeconds", 30));

            // Load Privacy configurations
            _hashWindowTitles = config.GetValue("Privacy:HashWindowTitles", false);
            _removeSensitiveKeywords = config.GetValue("Privacy:RemoveSensitiveKeywords", true);
            _sensitiveKeywords = config.GetSection("Privacy:SensitiveKeywords").GetChildren()
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList()!;
            if (_sensitiveKeywords.Count == 0)
            {
                _sensitiveKeywords = new List<string> { "password", "bank", "credit card", "ssn", "social security", "login", "sign in" };
            }
            _sensitiveReplacementText = config.GetValue("Privacy:SensitiveReplacementText", "[Sensitive Content]")!;

            // Configure HttpClient
            _httpClient.Timeout = _timeout;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "ActivityTracker/1.0");

            _logger.LogInformation("🤖 Telegram Bot Service initialized");
            _logger.LogDebug("Chat ID: {ChatId}, Privacy Mode: {PrivacyMode}, Hash Window Titles: {HashTitles}, Remove Sensitive: {RemoveSensitive}",
                _chatId, _enablePrivacyMode, _hashWindowTitles, _removeSensitiveKeywords);
        }

        /// <summary>
        /// Validates that required configuration exists
        /// </summary>
        private void ValidateConfiguration(IConfiguration config)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(config["Telegram:BotToken"]))
                errors.Add("Telegram:BotToken is required. Get it from @BotFather on Telegram.");

            if (string.IsNullOrWhiteSpace(config["Telegram:ChatId"]))
                errors.Add("Telegram:ChatId is required. Get it by messaging @userinfobot on Telegram.");

            if (errors.Any())
            {
                var errorMessage = string.Join(Environment.NewLine, errors);
                _logger?.LogCritical("❌ Configuration validation failed: {Errors}", errorMessage);
                throw new InvalidOperationException($"Telegram configuration errors:{Environment.NewLine}{errorMessage}");
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Sends an activity report to Telegram
        /// </summary>
        /// <param name="report">The activity report to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if sent successfully, false otherwise</returns>
        public async Task<bool> SendReportAsync(ActivityReport report, CancellationToken cancellationToken = default)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));

            try
            {
                _logger.LogDebug("📨 Attempting to send report {Id} ({App})",
                    report.Id, report.AppName);

                var message = FormatMessage(report);
                var success = await SendTelegramMessageAsync(message, isPreEscaped: true, cancellationToken);

                if (success)
                {
                    _logger.LogDebug("✅ Report sent successfully: {App} ({Duration})",
                        report.AppName, DurationFormatter.Format(report.DurationSeconds));
                }
                else
                {
                    _logger.LogWarning("⚠️ Failed to send report: {App} (Attempt will be retried)",
                        report.AppName);
                }

                return success;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⏹️ Sending cancelled for report {Id}", report.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending report {Id}", report.Id);
                return false;
            }
        }

        /// <summary>
        /// Sends an arbitrary system notification message to Telegram
        /// </summary>
        public async Task<bool> SendSystemNotificationAsync(string message, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return await SendTelegramMessageAsync(message, isPreEscaped: false, cancellationToken);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Formats the activity report into a Telegram message
        /// </summary>
        private string FormatMessage(ActivityReport report)
        {
            try
            {
                // Handle window title privacy
                string windowTitle = report.WindowTitle;
                if (_enablePrivacyMode && !string.IsNullOrEmpty(windowTitle))
                {
                    // Option 1: Replace with generic text
                    windowTitle = "[Window Title Hidden for Privacy]";
                }
                else if (!string.IsNullOrEmpty(windowTitle))
                {
                    // Option 2: Remove/replace sensitive keywords if found
                    if (_removeSensitiveKeywords)
                    {
                        bool containsSensitive = _sensitiveKeywords.Any(k => 
                            windowTitle.Contains(k, StringComparison.OrdinalIgnoreCase));
                        if (containsSensitive)
                        {
                            windowTitle = _sensitiveReplacementText;
                        }
                    }

                    // Option 3: Hash window title
                    if (_hashWindowTitles && windowTitle != _sensitiveReplacementText)
                    {
                        windowTitle = HashString(windowTitle);
                    }
                }

                // ✅ FIXED: Use DurationFormatter here
                string durationString = DurationFormatter.Format(report.DurationSeconds);

                // Escape variables individually to prevent unclosed formatting tags in titles/apps
                string escapedAppName = EscapeMarkdown(report.AppName);
                string escapedWindowTitle = EscapeMarkdown(windowTitle);
                string escapedDuration = EscapeMarkdown(durationString);
                string escapedUserName = EscapeMarkdown(report.UserName);

                // Build message
                var message = $"""
                🕒 *Activity Report*
                
                *User*: `{escapedUserName}`
                *Application*: `{escapedAppName}`
                *Window Title*: {escapedWindowTitle}
                *Duration*: {escapedDuration}
                *Time*: {report.Timestamp:HH:mm}
                *Date*: {report.Timestamp:yyyy-MM-dd}
                """;

                // Truncate if too long (Telegram has 4096 character limit)
                if (message.Length > _maxMessageLength)
                {
                    message = message.Substring(0, _maxMessageLength - 3) + "...";
                    _logger.LogDebug("📝 Message truncated to {Length} characters", _maxMessageLength);
                }

                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error formatting message for report {Id}", report.Id);
                return $"⚠️ Error formatting activity report for {report.AppName}";
            }
        }

        /// <summary>
        /// Escapes markdown special characters for Telegram
        /// </summary>
        private string EscapeMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            // Telegram MarkdownV2 requires escaping these characters: _ * [ ] ( ) ~ ` > # + - = | { } . !
            var specialChars = new[] { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };

            var result = new StringBuilder(text);
            foreach (var c in specialChars)
            {
                result.Replace(c.ToString(), $"\\{c}");
            }

            return result.ToString();
        }

        /// <summary>
        /// Sends a message to Telegram
        /// </summary>
        private async Task<bool> SendTelegramMessageAsync(string message, bool isPreEscaped = false, CancellationToken cancellationToken = default)
        {
            try
            {
                var payload = new
                {
                    chat_id = _chatId,
                    text = isPreEscaped ? message : EscapeNonFormattingMarkdown(message),
                    parse_mode = "MarkdownV2",
                    disable_notification = false,
                    disable_web_page_preview = true
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var url = $"{TelegramApiBaseUrl}{_botToken}/sendMessage";

                _logger.LogTrace("🌐 Sending Telegram request to {Url}", url);

                var response = await _httpClient.PostAsync(url, content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogTrace("✅ Telegram API responded with success");
                    return true;
                }

                // Log the error details
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("⚠️ Telegram API error: {StatusCode}, Response: {Response}",
                    response.StatusCode, responseBody);

                // Handle specific HTTP status codes
                switch (response.StatusCode)
                {
                    case System.Net.HttpStatusCode.Unauthorized:
                        _logger.LogError("❌ Invalid bot token. Please check Telegram:BotToken in configuration.");
                        break;
                    case System.Net.HttpStatusCode.BadRequest:
                        _logger.LogError("❌ Bad request to Telegram API. Check chat ID and message format.");
                        break;
                    case System.Net.HttpStatusCode.TooManyRequests:
                        _logger.LogWarning("⏳ Rate limited by Telegram. Will retry with backoff.");
                        break;
                    case System.Net.HttpStatusCode.NotFound:
                        _logger.LogError("❌ Chat not found. Please check Telegram:ChatId in configuration.");
                        break;
                }

                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "🌐 Network error sending to Telegram: {Message}", ex.Message);
                return false;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "📄 JSON serialization error");
                return false;
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("⏹️ Telegram send cancelled");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "⏱️ Telegram send timeout after {Timeout}", _timeout);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Unexpected error sending to Telegram");
                return false;
            }
        }

        #endregion

        private static string HashString(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hashBytes).ToLower().Substring(0, 16) + "...";
        }

        #region Helper Methods

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
    }
}