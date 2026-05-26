using System;
using System.Text.Json.Serialization;

namespace Activity_Tracker
{
    /// <summary>
    /// Represents an activity session with sync tracking for offline capabilities
    /// </summary>
    public class ActivityReport
    {
        #region Properties

        /// <summary>
        /// Unique identifier for this report (required for tracking sync status)
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// When the activity session started
        /// </summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Name of the application (process name)
        /// </summary>
        [JsonPropertyName("app")]
        public string AppName { get; set; } = string.Empty;

        /// <summary>
        /// Window title (may contain sensitive information)
        /// </summary>
        [JsonPropertyName("title")]
        public string WindowTitle { get; set; } = string.Empty;

        /// <summary>
        /// Duration of the session in seconds
        /// </summary>
        [JsonPropertyName("duration")]
        public int DurationSeconds { get; set; }

        /// <summary>
        /// Windows username of the account being tracked
        /// </summary>
        [JsonPropertyName("user")]
        public string UserName { get; set; } = Environment.UserName;

        #endregion

        #region Sync Status Properties

        /// <summary>
        /// Whether this report has been successfully sent to Telegram
        /// </summary>
        [JsonPropertyName("sent")]
        public bool Sent { get; set; } = false;

        /// <summary>
        /// Number of attempts made to send this report
        /// </summary>
        [JsonPropertyName("attempts")]
        public int SendAttempts { get; set; } = 0;

        /// <summary>
        /// When the last send attempt was made (null if never attempted)
        /// </summary>
        [JsonPropertyName("lastAttempt")]
        public DateTime? LastSendAttempt { get; set; }

        /// <summary>
        /// Error message from the last failed attempt (if any)
        /// </summary>
        [JsonPropertyName("error")]
        public string? LastError { get; set; }

        /// <summary>
        /// When this report was created (used for cleanup)
        /// </summary>
        [JsonPropertyName("created")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        #endregion

        #region Computed Properties

        /// <summary>
        /// Whether this report should be retried based on attempts and timing
        /// </summary>
        [JsonIgnore]
        public bool ShouldRetry => !Sent && SendAttempts < MaxRetryAttempts;

        /// <summary>
        /// The next time this report should be retried (based on exponential backoff)
        /// Returns null if report is already sent or has exceeded max attempts
        /// </summary>
        [JsonIgnore]
        public DateTime? NextRetryTime
        {
            get
            {
                if (Sent || !LastSendAttempt.HasValue || SendAttempts >= MaxRetryAttempts)
                    return null;

                return CalculateNextRetryTime();
            }
        }

        /// <summary>
        /// Formatted duration as TimeSpan for display
        /// </summary>
        [JsonIgnore]
        public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);

        /// <summary>
        /// Maximum number of retry attempts before giving up
        /// </summary>
        [JsonIgnore]
        private const int MaxRetryAttempts = 10;

        #endregion

        #region Constructors

        /// <summary>
        /// Default constructor for serialization
        /// </summary>
        public ActivityReport() { }

        /// <summary>
        /// Creates a report from an AppSession
        /// </summary>
        public static ActivityReport FromSession(AppSession session)
        {
            return new ActivityReport
            {
                Timestamp = session.StartTime,
                AppName = session.AppName,
                WindowTitle = session.WindowTitle,
                DurationSeconds = (int)session.Duration.TotalSeconds,
                UserName = Environment.UserName,
                CreatedAt = DateTime.Now
            };
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Calculates the next retry time using exponential backoff
        /// </summary>
        private DateTime CalculateNextRetryTime()
        {
            if (!LastSendAttempt.HasValue || SendAttempts == 0)
                return DateTime.Now.AddMinutes(1); // First retry after 1 minute

            // Exponential backoff schedule in minutes
            int delayMinutes = SendAttempts switch
            {
                1 => 5,    // 5 minutes after first failure
                2 => 15,   // 15 minutes after second failure
                3 => 60,   // 1 hour after third failure
                4 => 180,  // 3 hours after fourth failure
                5 => 360,  // 6 hours after fifth failure
                _ => 1440  // 24 hours after sixth+ failure
            };

            return LastSendAttempt.Value.AddMinutes(delayMinutes);
        }

        /// <summary>
        /// Records a send attempt (successful or not)
        /// </summary>
        public void RecordSendAttempt(bool success, string? error = null)
        {
            SendAttempts++;
            LastSendAttempt = DateTime.Now;

            if (success)
            {
                Sent = true;
                LastError = null;
            }
            else
            {
                LastError = error ?? "Unknown error";
            }
        }

        /// <summary>
        /// Checks if this report is ready for retry (respects backoff timing)
        /// </summary>
        public bool IsReadyForRetry()
        {
            if (!ShouldRetry) return false;
            if (SendAttempts == 0) return true; // Never attempted

            var nextRetry = CalculateNextRetryTime();
            return DateTime.Now >= nextRetry;
        }

        /// <summary>
        /// Creates a copy of this report (useful for testing and archiving)
        /// </summary>
        public ActivityReport Clone()
        {
            return new ActivityReport
            {
                Id = Id,
                Timestamp = Timestamp,
                AppName = AppName,
                WindowTitle = WindowTitle,
                DurationSeconds = DurationSeconds,
                UserName = UserName,
                Sent = Sent,
                SendAttempts = SendAttempts,
                LastSendAttempt = LastSendAttempt,
                LastError = LastError,
                CreatedAt = CreatedAt
            };
        }

        #endregion

        #region Overrides

        public override string ToString()
        {
            var duration = TimeSpan.FromSeconds(DurationSeconds);
            return $"{AppName} ({duration:mm\\:ss}) at {Timestamp:HH:mm} - {(Sent ? "Sent" : "Pending")}";
        }

        #endregion
    }
}