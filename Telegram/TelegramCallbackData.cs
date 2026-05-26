namespace Activity_Tracker.Telegram;

/// <summary>
/// Short callback tokens for inline keyboards (max 64 bytes per Telegram rules).
/// </summary>
public static class TelegramCallbackData
{
    // Navigation
    public const string NavMain = "nav:main";
    public const string NavMore = "nav:more";
    public const string NavHelp = "nav:help";

    // Control
    public const string Track = "c:track";
    public const string Stop = "c:stop";
    public const string Status = "c:status";
    public const string Current = "c:current";

    // Data
    public const string Stats = "d:stats";
    public const string Pending = "d:pending";
    public const string Sync = "d:sync";
    public const string Clear = "d:clear";

    // Utility
    public const string Screenshot = "u:shot";
    public const string Webcam = "u:cam";
    public const string Sysinfo = "u:sys";
    public const string Processes = "u:proc";
    public const string Users = "u:users";
    public const string Lock = "u:lock";
    public const string Sleep = "u:sleep";
    public const string Blocklist = "u:block";

    // Confirm / destructive
    public const string ConfirmShutdown = "cfm:shutdown";
    public const string ConfirmShutdownYes = "cfm:shutdown:yes";
    public const string ConfirmShutdownNo = "cfm:shutdown:no";
}
