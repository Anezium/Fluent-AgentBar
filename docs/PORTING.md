# Porting Notes

## Goal

Build Codex SWBar Windows as a native Windows version of the parts of CodexBar that matter here:

- Codex usage/account visibility.
- Claude usage/account visibility.
- Multiple isolated Codex profiles/sessions.
- Small taskbar-first UI with a tray fallback.

## Chosen Stack

This scaffold uses plain Win32 + C++17 because the machine has MinGW-w64 available but no .NET SDK, MSBuild, or CMake. That keeps the app native and buildable immediately.

Later, if we want richer UI, the provider layer can move unchanged behind:

- WPF/WinUI 3 after installing the .NET SDK/Windows App SDK, or
- a C++/WinUI shell if Visual Studio Build Tools are installed.

## Taskbar Presence

The primary UI is a compact `TaskbarPresence` HWND. The app creates one presence
for each detected primary or secondary Windows taskbar, converts each presence to
a `WS_CHILD` window, and parents it under Explorer's `Shell_TrayWnd` or
`Shell_SecondaryTrayWnd`. `TrayNotifyWnd` is used only as a geometry anchor so
the Codex presence sits near the system tray. The tray icon remains as a fallback
because these Explorer class names are not a public shell extension contract.

Clicking the presence toggles a `CodexBar` popup. The popup is an owned Win32
`WS_POPUP`/`WS_EX_TOOLWINDOW` window with GDI-rendered Fluent-style surfaces,
using the same `UsageRow` data that powers refresh logs and settings.

The main top-level window remains hidden as the message host. A separate Settings
window owns profile and provider configuration. Starting the executable again
signals the existing instance with `WM_SHOW_SETTINGS` instead of launching a
second background app.

## Codex

Codex is queried through:

```text
codex -s read-only -a untrusted app-server
```

The app sends JSON-RPC lines over stdio:

- `initialize`
- `account/read`
- `account/rateLimits/read`

`account/read` returns ChatGPT account identity/plan. `account/rateLimits/read` returns primary and secondary usage windows when the stdio session is kept open long enough for the async quota lookup to finish.

Troubleshooting note: older local config may contain an invalid `~/.codex/config.toml` value:

```text
unknown variant `default`, expected `fast` or `flex`
```

Codex falls back to defaults and `account/read` still works, but quota debugging should be repeated after that config is corrected.

Multi-profile support is implemented by setting `CODEX_HOME` per configured profile before launching `codex app-server`. The default profile lives under `%APPDATA%\Codex SWBar Windows\profiles\main`, not under `%USERPROFILE%\.codex`, so Codex SWBar Windows does not switch or log out the user's normal Codex Desktop/CLI profile. Profiles can be added, renamed, enabled/disabled, opened, and logged into from Settings; the JSON config remains the backing store.

Profile login uses app-server auth:

```json
{ "method": "account/login/start", "id": 2, "params": { "type": "chatgpt" } }
```

The app opens the returned `authUrl`, keeps app-server alive for the localhost callback, and waits for `account/login/completed`. The profile's `config.toml` is initialized with `cli_auth_credentials_store = "file"` so credentials stay inside that profile directory.

## Claude

The first Claude provider is intentionally shallow:

- checks `claude --version`;
- checks known credential files;
- reports a ready/pending state.

Next bridge options:

- OAuth usage endpoint, using Claude credentials where available.
- Manual session cookie mode.
- PTY `/usage` automation.
- Local JSONL cost scan.

## Config

Runtime config lives at:

```text
%APPDATA%\Codex SWBar Windows\config.json
```

The app creates a default config on first run.
The Settings action writes refresh timing and Claude enablement back to this file.
