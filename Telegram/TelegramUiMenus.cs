using Telegram.Bot.Types.ReplyMarkups;

namespace Activity_Tracker.Telegram;

/// <summary>
/// Inline keyboard layouts for the Telegram control panel.
/// </summary>
public static class TelegramUiMenus
{
    public static InlineKeyboardMarkup MainMenu => new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("▶️ Track", TelegramCallbackData.Track),
            InlineKeyboardButton.WithCallbackData("⏸️ Stop", TelegramCallbackData.Stop),
            InlineKeyboardButton.WithCallbackData("📊 Status", TelegramCallbackData.Status),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("👁️ Current", TelegramCallbackData.Current),
            InlineKeyboardButton.WithCallbackData("📈 Stats", TelegramCallbackData.Stats),
            InlineKeyboardButton.WithCallbackData("⏳ Pending", TelegramCallbackData.Pending),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("📸 Screenshot", TelegramCallbackData.Screenshot),
            InlineKeyboardButton.WithCallbackData("🔄 Sync", TelegramCallbackData.Sync),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🛠️ More tools", TelegramCallbackData.NavMore),
            InlineKeyboardButton.WithCallbackData("❓ Help", TelegramCallbackData.NavHelp),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🛑 Shutdown", TelegramCallbackData.ConfirmShutdown),
        },
    });

    public static InlineKeyboardMarkup MoreToolsMenu => new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("💻 Sysinfo", TelegramCallbackData.Sysinfo),
            InlineKeyboardButton.WithCallbackData("⚙️ Processes", TelegramCallbackData.Processes),
            InlineKeyboardButton.WithCallbackData("👥 Users", TelegramCallbackData.Users),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("📷 Webcam", TelegramCallbackData.Webcam),
            InlineKeyboardButton.WithCallbackData("🔒 Lock", TelegramCallbackData.Lock),
            InlineKeyboardButton.WithCallbackData("💤 Sleep", TelegramCallbackData.Sleep),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🛡️ Blocklist", TelegramCallbackData.Blocklist),
            InlineKeyboardButton.WithCallbackData("🗑️ Clear pending", TelegramCallbackData.Clear),
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("⬅️ Back", TelegramCallbackData.NavMain),
        },
    });

    public static InlineKeyboardMarkup ShutdownConfirmMenu => new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("✅ Confirm shutdown", TelegramCallbackData.ConfirmShutdownYes),
            InlineKeyboardButton.WithCallbackData("❌ Cancel", TelegramCallbackData.ConfirmShutdownNo),
        },
    });

    public static string MainMenuText(string? userName = null)
    {
        var greeting = string.IsNullOrEmpty(userName) ? "Activity Tracker" : $"Hello, {EscapeHtml(userName)}";
        return $"""
            <b>🕵️‍♂️ {greeting}</b>

            Use the buttons below to control monitoring remotely.
            Slash commands still work (e.g. <code>/kill 1234</code>).
            """;
    }

    public static string MoreToolsText() =>
        """
        <b>🛠️ More tools</b>

        Tap an action or use slash commands for advanced options:
        • <code>/history 10</code> — browser history
        • <code>/msg text</code> — on-screen alert
        • <code>/open url</code> — open URL
        • <code>/kill pid</code> — terminate process
        • <code>/block name</code> — block app or site
        """;

    public static string HelpText() =>
        """
        <b>❓ Help</b>

        <b>Control:</b> Track, Stop, Status, Current
        <b>Data:</b> Stats, Pending, Sync, Clear
        <b>Utility:</b> Screenshot, Webcam, Sysinfo, Lock, Sleep
        <b>Advanced (slash only):</b>
        <code>/kill</code> <code>/block</code> <code>/msg</code> <code>/open</code> <code>/history</code>

        Tap <b>Main menu</b> or send <code>/menu</code> anytime.
        """;

    public static string ShutdownConfirmText() =>
        """
        <b>⚠️ Shut down Activity Tracker?</b>

        The app will stop on this PC. Activity reports will pause until it runs again.
        """;

    public static string EscapeHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
