using System;
using System.Text.Json.Serialization;

namespace Activity_Tracker
{
    /// <summary>
    /// Represents a Windows Toast notification captured by the application
    /// </summary>
    public class NotificationReport
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.Now;

        [JsonPropertyName("app")]
        public string AppName { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public string UserName { get; set; } = Environment.UserName;
    }
}
