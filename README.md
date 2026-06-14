# Fluent AgentBar

WinUI 3 Windows taskbar presence inspired by CodexBar, scoped to Codex and Claude.

Active app: `winui\FluentAgentBar.csproj`. Active verification: `.\scripts\verify-winui.ps1`.
The legacy C++ implementation in `src/` and `build.ps1` is kept only as a historical reference and should not be used for new WinUI validation.

This app provides:

- WinUI 3 taskbar widget and DPI-aware flyout for high-resolution Windows displays.
- Windows 11 Fluent surfaces with Mica-like settings and Acrylic-like flyouts.
- Explorer taskbar integration through top-level owned transparent widgets.
- One taskbar presence per detected primary/secondary Windows taskbar.
- Multi-provider profile support through separate `CODEX_HOME` / `CLAUDE_CONFIG_DIR` directories.
- Codex account discovery through `codex app-server`.
- Codex login per isolated profile through app-server browser auth.
- Settings controls for startup, profile creation, rename, enable/disable, folder open, and refresh config.
- Claude usage through configured Claude CLI credential directories.
- Local token and USD cost summaries from Codex and Claude session logs.
- Local config at `%APPDATA%\Fluent AgentBar\config.json`.

On first run, Fluent AgentBar may copy a legacy config from
`%APPDATA%\Codex SWBar Windows\config.json` when the current config file does
not exist. The active config path remains
`%APPDATA%\Fluent AgentBar\config.json`.

## Build

Requires the .NET 8 SDK.

```powershell
dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64
```

The executable is written to `winui\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\FluentAgentBar.exe`.

Run the WinUI test suite with:

```powershell
dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug
```

Run the full WinUI verification baseline with:

```powershell
.\scripts\verify-winui.ps1
```

`build.ps1` is the legacy C++ build path only; it is not the validation command for new WinUI work.

## Run

```powershell
.\winui\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\FluentAgentBar.exe
```

The app starts in the background and creates a compact usage widget for each detected Windows taskbar. Left-click any widget to open the shared usage flyout. Right-click a widget for a context menu with Refresh now, Settings, Open config file, quick toggles (start with Windows, widget glow, acrylic flyout), and Exit. Launching the executable again focuses the existing instance's Settings window. Use `--show-settings` to show Settings explicitly, or `--show-flyout` to show the usage flyout.

## Multi-provider Profiles

Use Settings to add, rename, enable/disable, and log in profiles. `Settings`
updates refresh timing and the Claude provider toggle without opening the JSON.

You can still edit `%APPDATA%\Fluent AgentBar\config.json` directly:

```json
{
  "refreshIntervalSeconds": 300,
  "flyoutStyle": "acrylic",
  "profiles": [
    {
      "provider": "codex",
      "label": "Main",
      "home": "%APPDATA%\\Fluent AgentBar\\profiles\\main",
      "enabled": true
    },
    {
      "provider": "codex",
      "label": "Work",
      "home": "%APPDATA%\\Fluent AgentBar\\profiles\\work",
      "enabled": true
    },
    {
      "provider": "claude",
      "label": "Personal",
      "home": "%USERPROFILE%\\.claude",
      "enabled": true
    }
  ]
}
```

Each Codex profile is refreshed with its own `CODEX_HOME`. Each Claude profile
is refreshed with its own `CLAUDE_CONFIG_DIR`; the default Claude profile uses
`%USERPROFILE%\.claude` so an existing Claude CLI login is reused.

Use the profile card `Login` button to authenticate a profile. The app keeps
Codex credentials in that profile's `CODEX_HOME`, so it does not log out or
modify your normal `%USERPROFILE%\.codex` session. Claude login runs
`claude /login` with the profile's `CLAUDE_CONFIG_DIR`.

## Current Limitations

- Codex account and quota RPC are wired through `codex app-server`.
- Codex profile login is implemented with app-server browser auth for the current local Codex CLI version.
- Claude quota parsing is wired through local Claude OAuth credentials.
- Cookie import and OAuth repair flows are not ported yet.
