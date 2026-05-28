using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Activity_Tracker.Telegram;

/// <summary>
/// Routes Telegram messages and inline keyboard callbacks to command handlers.
/// </summary>
public class TelegramUpdateDispatcher
{
    private readonly TelegramCommandService _commands;
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramUpdateDispatcher> _logger;

    public TelegramUpdateDispatcher(
        TelegramCommandService commands,
        ITelegramBotClient botClient,
        ILogger<TelegramUpdateDispatcher> logger)
    {
        _commands = commands;
        _botClient = botClient;
        _logger = logger;
    }

    public async Task DispatchAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery != null)
        {
            await DispatchCallbackAsync(update.CallbackQuery, cancellationToken);
            return;
        }

        if (update.Message?.Text != null)
        {
            await DispatchMessageAsync(update.Message, cancellationToken);
        }
    }

    private async Task DispatchMessageAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.Chat == null || message.From == null) return;

        var chatId = message.Chat.Id.ToString();
        var userName = message.From.Username ?? message.From.FirstName ?? "User";
        var text = message.Text!.Trim();

        _logger.LogInformation("📩 Command from {User}: {Command}", userName, text);

        if (!_commands.IsAuthorized(chatId))
        {
            _logger.LogWarning("🚫 Unauthorized access attempt from chat ID: {ChatId}", chatId);
            await _commands.SendPlainMessageAsync("⛔ You are not authorized to use this bot.", chatId, cancellationToken);
            return;
        }

        var lower = text.ToLowerInvariant();
        if (lower is "/start" or "/menu")
        {
            await _commands.SendMainMenuAsync(chatId, userName, cancellationToken);
            return;
        }

        await _commands.ProcessTextCommandAsync(chatId, userName, text, cancellationToken);
    }

    private async Task DispatchCallbackAsync(CallbackQuery callback, CancellationToken cancellationToken)
    {
        var data = callback.Data;
        if (string.IsNullOrEmpty(data) || callback.Message?.Chat == null)
        {
            await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
            return;
        }

        var chatId = callback.Message.Chat.Id.ToString();
        var userName = callback.From.Username ?? callback.From.FirstName ?? "User";
        var messageId = callback.Message.MessageId;

        if (!_commands.IsAuthorized(chatId))
        {
            await AnswerCallbackSafeAsync(callback.Id, "Not authorized", cancellationToken);
            return;
        }

        _logger.LogInformation("🔘 Callback from {User}: {Data}", userName, data);

        try
        {
            switch (data)
            {
                case TelegramCallbackData.NavMain:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.EditMenuAsync(chatId, messageId, TelegramUiMenus.MainMenuText(userName), TelegramUiMenus.MainMenu, cancellationToken);
                    break;

                case TelegramCallbackData.NavMore:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.EditMenuAsync(chatId, messageId, TelegramUiMenus.MoreToolsText(), TelegramUiMenus.MoreToolsMenu, cancellationToken);
                    break;

                case TelegramCallbackData.NavHelp:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.EditMenuAsync(chatId, messageId, TelegramUiMenus.HelpText(), TelegramUiMenus.MainMenu, cancellationToken);
                    break;

                case TelegramCallbackData.ConfirmShutdown:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.EditMenuAsync(chatId, messageId, TelegramUiMenus.ShutdownConfirmText(), TelegramUiMenus.ShutdownConfirmMenu, cancellationToken);
                    break;

                case TelegramCallbackData.ConfirmShutdownNo:
                    await AnswerCallbackSafeAsync(callback.Id, "Cancelled", cancellationToken);
                    await _commands.EditMenuAsync(chatId, messageId, TelegramUiMenus.MainMenuText(userName), TelegramUiMenus.MainMenu, cancellationToken);
                    break;

                case TelegramCallbackData.ConfirmShutdownYes:
                    await AnswerCallbackSafeAsync(callback.Id, "Shutting down…", cancellationToken);
                    await _commands.HandleShutdownCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Track:
                    await AnswerCallbackSafeAsync(callback.Id, "Tracking started", cancellationToken);
                    await _commands.HandleTrackCommandAsync(chatId, userName, cancellationToken);
                    break;

                case TelegramCallbackData.Stop:
                    await AnswerCallbackSafeAsync(callback.Id, "Tracking stopped", cancellationToken);
                    await _commands.HandleStopCommandAsync(chatId, userName, cancellationToken);
                    break;

                case TelegramCallbackData.Status:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandleStatusCommandAsync(chatId, userName, cancellationToken);
                    break;

                case TelegramCallbackData.Current:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandleCurrentCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Stats:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandleStatisticsCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Pending:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandlePendingCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Clear:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandleClearCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Screenshot:
                    await AnswerCallbackSafeAsync(callback.Id, "Capturing screenshot…", cancellationToken);
                    await _commands.HandleScreenshotCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Webcam:
                    await AnswerCallbackSafeAsync(callback.Id, "Capturing webcam…", cancellationToken);
                    await _commands.HandleWebcamCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Sysinfo:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandleSysInfoCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Processes:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandleProcessesCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Users:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandleUsersCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Lock:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandleLockCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Sleep:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandleSleepCommandAsync(chatId, cancellationToken);
                    break;

                case TelegramCallbackData.Blocklist:
                    await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    await _commands.HandleBlockCommandAsync(chatId, "/block", cancellationToken);
                    break;

                default:
                    // Handle whoami callbacks
                    if (data.StartsWith("whoami_"))
                    {
                        if (data.StartsWith("whoami_confirmed_"))
                        {
                            var username = data.Replace("whoami_confirmed_", "");
                            await AnswerCallbackSafeAsync(callback.Id, $"Confirmed: {username}", cancellationToken);
                        }
                        else if (data == "whoami_refresh")
                        {
                            await AnswerCallbackSafeAsync(callback.Id, "Refreshing…", cancellationToken);
                            await _commands.HandleWhoamiCommandAsync(chatId, userName, cancellationToken);
                        }
                        else
                        {
                            await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                        }
                    }
                    else
                    {
                        await AnswerCallbackSafeAsync(callback.Id, cancellationToken);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error handling callback {Data}", data);
            await AnswerCallbackSafeAsync(callback.Id, "Error", cancellationToken);
        }
    }

    private Task AnswerCallbackSafeAsync(string callbackQueryId, CancellationToken cancellationToken) =>
        AnswerCallbackSafeAsync(callbackQueryId, null, cancellationToken);

    private async Task AnswerCallbackSafeAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
    {
        try
        {
            await _botClient.AnswerCallbackQuery(
                callbackQueryId,
                text: text,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AnswerCallbackQuery failed");
        }
    }
}
