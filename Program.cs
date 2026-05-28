using Activity_Tracker;
using Activity_Tracker.Telegram;
using Telegram.Bot;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// Declare DPI Awareness for accurate screen coordinates during screenshots
NativeMethods.SetProcessDPIAware();

// If started from a terminal (e.g. dotnet run), re-launch detached so no console stays open
if (TryRelaunchWithoutConsole(args))
    return;

// Prefer the build output folder (dotnet run / published exe) for config files
var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
if (File.Exists(Path.Combine(appDir, "telegram_config.json")) ||
    File.Exists(Path.Combine(appDir, "appsettings.json")))
{
    Directory.SetCurrentDirectory(appDir);
}

// Setup configuration path (after cwd may point at app output)
string configPath = Path.Combine(Directory.GetCurrentDirectory(), "telegram_config.json");
string currentToken = "";
string currentChatId = "";

// Load existing values from configuration if they exist
if (File.Exists(configPath))
{
    try
    {
        string jsonText = File.ReadAllText(configPath);
        currentToken = ExtractJsonValue(jsonText, "BotToken");
        currentChatId = ExtractJsonValue(jsonText, "ChatId");
    }
    catch { }
}

if (string.IsNullOrEmpty(currentToken) || string.IsNullOrEmpty(currentChatId))
{
    try
    {
        string appsettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        if (File.Exists(appsettingsPath))
        {
            string appsettingsText = File.ReadAllText(appsettingsPath);
            if (string.IsNullOrEmpty(currentToken)) currentToken = ExtractJsonValue(appsettingsText, "BotToken");
            if (string.IsNullOrEmpty(currentChatId)) currentChatId = ExtractJsonValue(appsettingsText, "ChatId");
        }
    }
    catch { }
}

// Check if credentials are empty, placeholders, or defaults
bool isPlaceholder = string.IsNullOrEmpty(currentToken) || 
                     string.IsNullOrEmpty(currentChatId) ||
                     currentToken.Contains("YOUR_BOT_TOKEN") || 
                     currentToken.Contains("INSERT_BOT_TOKEN") ||
                     currentChatId.Contains("YOUR_CHAT_ID");

bool forceSetup = args.Contains("--setup");

if (isPlaceholder || forceSetup)
{
    bool isSaved = false;
    string setupToken = "";
    string setupChat = "";

    // Spin up an STA Thread to safely host the WPF setup window
    var thread = new Thread(() =>
    {
        var app = new Application();
        var win = new SetupWindow(
            currentToken.Contains("8509883067:") ? "" : currentToken, 
            currentChatId == "555437484" ? "" : currentChatId
        );
        app.Run(win);

        if (win.IsConfigured)
        {
            setupToken = win.BotToken;
            setupChat = win.ChatId;
            isSaved = true;
        }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join(); // Wait for user configuration to finish

    if (isSaved)
    {
        try
        {
            string newConfig = $$"""
{
  "Telegram": {
    "BotToken": "{{setupToken}}",
    "ChatId": "{{setupChat}}"
  }
}
""";
            File.WriteAllText(configPath, newConfig);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save config file: {ex.Message}", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
    }
    else if (isPlaceholder)
    {
        // Cancelled on placeholder / initial run -> exit gracefully
        return;
    }
}

// ─── Persistence & Auto-Start Setup ──────────────────────────────────────────
bool installPersistence = args.Contains("--install-persistence");
bool uninstallPersistence = args.Contains("--uninstall-persistence");
bool skipPersistPrompt = args.Contains("--no-persist");

if (uninstallPersistence)
{
    StartupPersistence.Uninstall(showMessage: true);
    return;
}

if (installPersistence)
{
    // Elevated one-shot installer: Startup folder + Task Scheduler + Registry, then launch background instance
    try
    {
        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");
        string workingDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();

        StartupPersistence.Install(exePath, workingDir, silent: true);
        StartupPersistence.LaunchBackgroundInstance(exePath, workingDir);
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Failed to install auto-start:\n{ex.Message}",
            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    return;
}

// First-time flow: after Telegram setup, prompt for admin (UAC) to install auto-start
if (!StartupPersistence.IsInstalled && !skipPersistPrompt)
{
    bool shouldElevate = false;

    var adminThread = new Thread(() =>
    {
        var win = new AdminNoticeWindow();
        win.ShowDialog();
        shouldElevate = win.UserAccepted;
    });
    adminThread.SetApartmentState(ApartmentState.STA);
    adminThread.Start();
    adminThread.Join();

    if (shouldElevate)
    {
        try
        {
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine executable path.");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--install-persistence",
                Verb = "runas",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory()
            };

            Process.Start(psi);
            return; // Elevated installer will start the background tracker
        }
        catch (Win32Exception)
        {
            // User cancelled UAC — continue without auto-start
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not request admin privileges:\n{ex.Message}\n\nThe tracker will run now without auto-start.",
                "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

// Create the host builder
var builder = Host.CreateApplicationBuilder(args);

// Configuration
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("telegram_config.json", optional: true, reloadOnChange: true) // Merges active configuration
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

// Register services with proper lifecycle

// 1. Singleton services (shared instance)
builder.Services.AddSingleton<LocalStorageService>();
builder.Services.AddSingleton<TelegramBotService>();

// 2. Register Worker and NotificationWatcher as singletons, then as hosted services
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<Worker>());

builder.Services.AddSingleton<NotificationWatcher>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NotificationWatcher>());

// 3. Telegram bot client and command UI
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var token = config["Telegram:BotToken"]
        ?? throw new InvalidOperationException("Telegram:BotToken is missing");
    return new TelegramBotClient(token);
});
builder.Services.AddSingleton<TelegramCommandService>();
builder.Services.AddSingleton<TelegramUpdateDispatcher>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TelegramCommandService>());

// HTTP client for outbound activity reports only
builder.Services.AddHttpClient<TelegramBotService>((serviceProvider, client) =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "ActivityTracker/1.0");
});

// Add logging configuration (no console — app runs headless in background)
builder.Logging.ClearProviders();
builder.Logging.AddDebug();
if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog();
}

// Enable Windows Service support if needed
if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "Activity Tracker Service";
    });
}

// Build and run the host
var host = builder.Build();

// Log startup information
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var env = host.Services.GetRequiredService<IHostEnvironment>();
var config = host.Services.GetRequiredService<IConfiguration>();

logger.LogInformation("🚀 Activity Tracker starting...");
logger.LogInformation("📁 Environment: {Environment}", env.EnvironmentName);
logger.LogInformation("📅 Current Directory: {Directory}", Directory.GetCurrentDirectory());

// Validate critical configuration
var botToken = config["Telegram:BotToken"];
var chatId = config["Telegram:ChatId"];

if (string.IsNullOrEmpty(botToken))
{
    logger.LogWarning("⚠️ Telegram:BotToken is not configured.");
}
else
{
    logger.LogDebug("✅ Telegram Bot Token configured");
}

if (string.IsNullOrEmpty(chatId))
{
    logger.LogWarning("⚠️ Telegram:ChatId is not configured.");
}
else
{
    logger.LogDebug("✅ Telegram Chat ID configured");
}

// Run the application
try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "💥 Application terminated unexpectedly");
    throw;
}
finally
{
    logger.LogInformation("👋 Activity Tracker shutting down...");
}

#region Helper Methods & UI Classes

/// <summary>
/// Re-launches the app without an attached console window (UseShellExecute).
/// </summary>
static bool TryRelaunchWithoutConsole(string[] args)
{
    if (args.Contains("--no-console"))
        return false;

    if (Debugger.IsAttached)
        return false;

    if (NativeMethods.GetConsoleWindow() == IntPtr.Zero)
        return false;

    string? exePath = Environment.ProcessPath;
    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        return false;

    var passthrough = args.Where(a => a != "--no-console").Append("--no-console").ToArray();
    string argumentList = string.Join(" ", passthrough.Select(QuoteProcessArgument));

    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = argumentList,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true
        });
        return true;
    }
    catch
    {
        NativeMethods.HideConsoleWindow();
        return false;
    }
}

static string QuoteProcessArgument(string arg) =>
    arg.Contains(' ') || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;

static string ExtractJsonValue(string json, string key)
{
    var match = System.Text.RegularExpressions.Regex.Match(json, $"\"{key}\"\\s*:\\s*\"([^\"]*)\"");
    return match.Success ? match.Groups[1].Value : string.Empty;
}

/// <summary>
/// Elegant Modern Dark Mode WPF setup window
/// </summary>
public class SetupWindow : Window
{
    public string BotToken { get; private set; } = string.Empty;
    public string ChatId { get; private set; } = string.Empty;
    public bool IsConfigured { get; private set; } = false;

    public SetupWindow(string existingToken, string existingChatId)
    {
        Title = "Activity Tracker Setup";
        Width = 460;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)); // Zinc-900 Dark Mode
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        // Main Grid layout
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Title
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Subtitle
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Inputs
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Spacer
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Button

        // Title
        var titleText = new TextBlock
        {
            Text = "🕵️‍♂️ Activity Tracker Bot Setup",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(129, 140, 248)), // Indigo-400
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(titleText, 0);
        grid.Children.Add(titleText);

        // Description Subtitle
        var subtitleText = new TextBlock
        {
            Text = "Please enter your Telegram credentials to establish remote control. This locks the application to your specific chat ID for security.",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)), // Zinc-400
            Margin = new Thickness(0, 0, 0, 20),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(subtitleText, 1);
        grid.Children.Add(subtitleText);

        // StackPanel for inputs
        var inputsPanel = new StackPanel();
        Grid.SetRow(inputsPanel, 2);

        inputsPanel.Children.Add(new TextBlock 
        { 
            Text = "Telegram Bot Token", 
            FontWeight = FontWeights.SemiBold, 
            FontSize = 12, 
            Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)), 
            Margin = new Thickness(0, 0, 0, 6) 
        });

        var tokenInput = new TextBox
        {
            Text = existingToken,
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.FromRgb(39, 39, 42)), // Zinc-800
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)), // Zinc-700
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 16)
        };
        inputsPanel.Children.Add(tokenInput);

        inputsPanel.Children.Add(new TextBlock 
        { 
            Text = "Authorized Chat ID (User ID)", 
            FontWeight = FontWeights.SemiBold, 
            FontSize = 12, 
            Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)), 
            Margin = new Thickness(0, 0, 0, 6) 
        });

        var chatIdInput = new TextBox
        {
            Text = existingChatId,
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 10)
        };
        inputsPanel.Children.Add(chatIdInput);

        grid.Children.Add(inputsPanel);

        // Save & Launch Button
        var saveBtn = new Button
        {
            Content = "Save & Continue",
            Padding = new Thickness(16, 12, 16, 12),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(79, 70, 229)), // Indigo-600 Accent
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        Grid.SetRow(saveBtn, 4);

        saveBtn.Click += (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(tokenInput.Text) || string.IsNullOrWhiteSpace(chatIdInput.Text))
            {
                MessageBox.Show("Please fill out both setup fields.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BotToken = tokenInput.Text.Trim();
            ChatId = chatIdInput.Text.Trim();
            IsConfigured = true;
            Close();
        };

        grid.Children.Add(saveBtn);
        Content = grid;
    }
}

/// <summary>
/// Dark-themed WPF window that explains the need for admin privileges
/// and lets the user accept (triggering UAC) or skip.
/// </summary>
public class AdminNoticeWindow : Window
{
    public bool UserAccepted { get; private set; } = false;

    public AdminNoticeWindow()
    {
        Title = "Activity Tracker — Administrator Setup";
        Width = 520;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        var grid = new Grid { Margin = new Thickness(28) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Icon + Title
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Description
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Bullet points
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Spacer
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons

        // Title
        var titleText = new TextBlock
        {
            Text = "🛡️ Administrator Privileges Required",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21)), // Amber-400
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(titleText, 0);
        grid.Children.Add(titleText);

        // Description
        var descText = new TextBlock
        {
            Text = "One-time administrator approval is required to register Activity Tracker " +
                   "in the Startup folder, Task Scheduler, and registry so it runs automatically " +
                   "in the background after you sign in.",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(descText, 1);
        grid.Children.Add(descText);

        // Bullet points explaining what admin enables
        var bulletPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(bulletPanel, 2);

        string[] bullets = new[]
        {
            "📂  Startup folder shortcut",
            "🔄  Task Scheduler (runs at sign-in)",
            "🔑  Registry Run key (auto-start)",
            "📡  Silent background operation after setup"
        };

        foreach (var bullet in bullets)
        {
            bulletPanel.Children.Add(new TextBlock
            {
                Text = bullet,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)),
                Margin = new Thickness(8, 4, 0, 4)
            });
        }

        grid.Children.Add(bulletPanel);

        // Buttons row
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetRow(btnPanel, 4);

        var skipBtn = new Button
        {
            Content = "Skip for Now",
            Padding = new Thickness(20, 10, 20, 10),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
            Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        skipBtn.Click += (s, e) =>
        {
            UserAccepted = false;
            Close();
        };
        btnPanel.Children.Add(skipBtn);

        var continueBtn = new Button
        {
            Content = "Continue as Admin  🛡️",
            Padding = new Thickness(20, 10, 20, 10),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(79, 70, 229)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        continueBtn.Click += (s, e) =>
        {
            UserAccepted = true;
            Close();
        };
        btnPanel.Children.Add(continueBtn);

        grid.Children.Add(btnPanel);
        Content = grid;
    }
}

/// <summary>
/// Helper class containing native Win32 methods
/// </summary>
internal static class NativeMethods
{
    private const int SwHide = 0;

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    public static extern bool FreeConsole();

    public static void HideConsoleWindow()
    {
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SwHide);
            FreeConsole();
        }
    }
}

#endregion