# Activity Tracker CLI

A Windows-based activity monitoring application that tracks application usage and sends reports to Telegram. Built with C# .NET 10, it runs as a background service and provides remote control via Telegram bot commands.

## Features

- **Window Monitoring**: Tracks active windows and application usage in real-time
- **Session Tracking**: Records duration of each application/window session
- **Telegram Integration**: Sends activity reports to your Telegram bot
- **Remote Control**: Control tracking via Telegram commands
- **Multi-User Support**: Works for all Windows accounts on the system
- **Offline Storage**: Stores reports locally when offline, syncs when connection restored
- **Privacy Features**: Optional window title hashing and sensitive keyword filtering
- **System Utilities**: Screenshot, webcam, system info, user management via Telegram

## Requirements

- Windows 10 or later
- .NET 10.0 Runtime (or use self-contained build)
- Telegram Bot Token (from @BotFather)
- Telegram Chat ID (from @userinfobot)

## Installation

### 1. Clone or Download

```bash
git clone <repository-url>
cd Activity Tracker CLI
```

### 2. Configure Telegram Credentials

**Option A: Use Setup Window**
```bash
dotnet run -- --setup
```
This opens a WPF window to enter your bot token and chat ID.

**Option B: Manual Configuration**
Create `telegram_config.json` in the project directory:
```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN",
    "ChatId": "YOUR_CHAT_ID"
  }
}
```

### 3. Build and Run

```bash
dotnet run
```

### 4. Install Auto-Start (Optional)

To start automatically when you sign in:
```bash
dotnet run -- --install-persistence
```

This requires administrator privileges and installs:
- System-wide Startup folder shortcut
- System-wide Registry Run key entry

## Configuration

Edit `appsettings.json` to customize behavior:

```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN",
    "ChatId": "YOUR_CHAT_ID",
    "EnablePrivacyMode": false,
    "AllowMultipleUsers": false,
    "TimeoutSeconds": 30
  },
  "Worker": {
    "CheckIntervalSeconds": 2,
    "MinSessionDurationSeconds": 15,
    "IgnoreSystemProcesses": true,
    "IgnoreProcesses": ["svchost.exe", "dwm.exe"]
  },
  "Privacy": {
    "HashWindowTitles": false,
    "RemoveSensitiveKeywords": true,
    "SensitiveKeywords": ["password", "bank", "credit card"]
  }
}
```

## Telegram Commands

### Control Commands
- `/track` - Start tracking
- `/stop` - Stop tracking
- `/status` - Show current tracking status
- `/whoami` - Show current Windows account being tracked
- `/persistence` - Manage auto-start settings

### Data Commands
- `/stats` - View detailed statistics
- `/history` - View activity history
- `/pending` - Show pending reports
- `/sync` - Manually sync pending reports
- `/clear` - Clear pending reports

### Utility Commands
- `/screenshot` - Capture screenshot
- `/webcam` - Capture webcam image
- `/sysinfo` - Show system information
- `/users` - List local user accounts
- `/processes` - Show running processes
- `/lock` - Lock workstation
- `/sleep` - Put computer to sleep

### Advanced Commands
- `/msg <text>` - Display message on screen
- `/open <url>` - Open URL in browser
- `/kill <process>` - Kill a process
- `/block <app>` - Block an application
- `/unblock <app>` - Unblock an application
- `/shutdown` - Shutdown computer

## Multi-User Support

The application supports tracking for all Windows accounts on the system:

- **System-wide Persistence**: Uses Common Startup folder and HKLM registry
- **User Identification**: Activity reports include Windows username
- **Session Isolation**: Each user session is tracked independently
- **Automatic Startup**: Starts automatically for each user when they sign in

### Installing on Another PC

1. **Build standalone exe:**
   ```bash
   dotnet publish -c Release -r win-x64 --self-contained
   ```

2. **Copy to target PC:**
   - Copy the `bin\Release\net10.0-windows\win-x64\publish\` folder
   - Transfer to the other PC

3. **Configure on target PC:**
   ```bash
   Activity Tracker.exe --setup
   Activity Tracker.exe --install-persistence
   ```

## Building for Production

### Self-Contained Build (includes .NET runtime)
```bash
dotnet publish -c Release -r win-x64 --self-contained
```

Output: `bin\Release\net10.0-windows\win-x64\publish\Activity Tracker.exe`

### Framework-Dependent Build (requires .NET runtime)
```bash
dotnet publish -c Release
```

## Command-Line Options

- `--setup` - Force Telegram setup window
- `--install-persistence` - Install auto-start (requires admin)
- `--uninstall-persistence` - Remove auto-start
- `--no-persist` - Skip auto-start prompt
- `--no-console` - Run without console window

## Troubleshooting

### Build Errors
- Ensure .NET 10.0 SDK is installed
- Run `dotnet restore` before building

### Telegram Not Receiving Reports
- Verify bot token and chat ID in configuration
- Check internet connection
- Use `/status` command to check pending reports
- Use `/sync` to manually sync pending reports

### Window Monitoring Not Working
- Ensure app is running in user session (not as SYSTEM service)
- Reinstall persistence: `--uninstall-persistence` then `--install-persistence`
- Check Windows Event Viewer for errors

### Multi-User Issues
- Each user needs the app to start in their session
- System-wide persistence (Common Startup + HKLM) handles this automatically
- Use `/whoami` to verify which account is being tracked

## Security Notes

- **Sensitive Files**: `telegram_config.json` and `appsettings.json` are excluded from Git
- **Privacy**: Window titles can be hashed or filtered for sensitive keywords
- **Authorization**: By default, only the configured chat ID can control the bot
- **Multi-User**: Set `AllowMultipleUsers: true` in config to allow multiple controllers

## Project Structure

```
Activity Tracker CLI/
├── Program.cs                    # Entry point and DI configuration
├── Worker.cs                      # Window monitoring logic
├── TelegramBotService.cs          # Telegram API integration
├── TelegramCommandService.cs      # Bot command handling
├── LocalStorageService.cs         # Data persistence
├── StartupPersistence.cs          # Auto-start management
├── ActivityReport.cs              # Activity data model
├── appsettings.json               # Configuration (excluded from Git)
├── telegram_config.json           # Telegram credentials (excluded from Git)
└── README.md                      # This file
```

## License


## Contributing

Contributions are welcome! Please ensure sensitive configuration files are never committed.
