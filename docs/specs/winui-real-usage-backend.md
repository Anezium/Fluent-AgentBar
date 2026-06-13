# Historical Spec: real Codex usage data in the WinUI app + Settings NumberBox bug

Legacy reference only. This spec records a past implementation task and may not
match the current WinUI code exactly.

Repo: `e:\Tools\Fluent-AgentBar`, project `winui\FluentAgentBar.csproj` (WinUI 3, net8.0-windows10.0.19041.0, x64).
Build check: `dotnet build winui/FluentAgentBar.csproj -c Debug -p:Platform=x64` → must end 0 errors / 0 warnings.
Do not commit.

## Task A — Replace mock usage with real Codex usage (the main job)

Today the WinUI app shows hardcoded numbers from `winui/MockUsageData.cs` (72/54/43/61/88/76) — the user sees "wrong usages" because nothing is real.

At the time this was written, the historical native C++ app implemented the real pipeline: it spawned `codex app-server` per profile (with that profile's `CODEX_HOME` env var), spoke JSON-RPC over stdio, and read account + rate-limit data. Use the current WinUI implementation and active docs as the source of truth for new work.

Implement in `winui/`:

1. `UsageService.cs` (new):
   - Async service that, for each enabled profile in `AppConfigStore.Load().CodexProfiles`, spawns `codex app-server` with `CODEX_HOME` set to the profile's expanded `codexHome` path (`Environment.ExpandEnvironmentVariables`), performs the JSON-RPC exchange, and extracts the two rate-limit windows: the ~5h window and the weekly window.
   - **Semantics matter**: the UI displays *remaining* percent (label `5h` → `RemainingPercent`, label `Weekly`/`Wk` → `WeeklyPercent`). If the RPC reports `used_percent`, convert: `remaining = 100 - used`.
   - Also fetch the plan/account label if the RPC exposes it (the native app shows plan names); fall back to the profile label and empty plan otherwise.
   - Timeout each profile refresh (10 s), kill the child process on timeout/dispose. Never block the UI thread (use `Task`, `Process` with async stdout reading).
   - Expose: `Task<IReadOnlyList<ProviderUsage>> FetchAsync()`, an event `Updated`, a `LastRefresh` timestamp, and a periodic refresh loop driven by `RefreshIntervalSeconds` from config (restart the loop when `AppConfigStore.Changed` fires).
   - On failure for a profile (not logged in, codex missing, RPC error): produce a `ProfileUsage` with `RemainingPercent = 0`, `WeeklyPercent = 0` and a way for the UI to show an unavailable state — add `bool IsAvailable` to `ProfileUsage` and render `--` instead of `0%` when false (update `RemainingText`/`WeeklyText` to return `"--"` when unavailable).
2. Wire it up in `App.xaml.cs` (single `UsageService` instance):
   - `FlyoutWindow`: `Providers` comes from the service; `LastRefreshText` from `LastRefresh`; the Refresh button (`OnRefreshClick`) triggers an immediate `FetchAsync`.
   - `TaskbarWidgetWindow`: the primary (first enabled) profile's real values replace the `MockUsageData.CreatePrimaryProfile` call. Update via `DispatcherQueue.TryEnqueue` on the `Updated` event.
   - Keep `MockUsageData.cs` only as a fallback for first paint before the first fetch completes (or delete it and start with the unavailable state — your call, keep it simple).
   - Claude stays a placeholder: keep the Claude card only if `config.Claude.Enabled`, with the unavailable (`--`) state for now.
3. Do not change the visual XAML layouts beyond what's needed for the `--` state.

**Careful with fragile UI code**: do not touch the DWM/window-style sequences in `TaskbarWidgetWindow.xaml.cs` / `FlyoutWindow.xaml.cs` (`_frameFixTimer`, `TransparentBackdrop`, `ThinAcrylicBackdrop`, watchdog). Only touch their data/binding paths.

If you cannot run `codex app-server` from your sandbox to test, that is fine: make the code defensive (null/missing JSON fields → unavailable state), and rely on the build for verification. The maintainer will test live.

## Task B — Settings "Refresh interval" NumberBox bug

Typing into the Refresh interval `NumberBox` (SettingsWindow.xaml, `RefreshIntervalCard`) misbehaves: a detached floating overlay with `X ^ v` buttons appears (see user report). Likely the WinUI `NumberBox` clear-button/spin overlay popup mispositioning, and/or invalid text handling.

Fix in `SettingsWindow.xaml` / `.cs`:
- Set `ValidationMode="InvalidInputOverwritten"` and keep `SpinButtonPlacementMode="Inline"`.
- Make sure the box is actually bound: on `ValueChanged`, clamp to [30, 3600] and persist to config (`AppConfigStore`) — check how the rest of SettingsWindow persists changes and follow the same pattern; the `Value="300"` literal suggests it may not be wired at all. Load the real value from config on open.
- If the floating overlay artifact persists with text input, disable the built-in delete/clear button (`IsDeleteButtonVisible="False"` via the underlying TextBox style is not exposed on NumberBox — acceptable alternative: handle invalid input via ValidationMode and leave the overlay alone if it is a toolkit popup bug; note your conclusion in the final summary).

## Verification

- `dotnet build winui/FluentAgentBar.csproj -c Debug -p:Platform=x64` → 0 errors, 0 warnings.
- Summarize: the exact RPC methods used, where the 5h/weekly windows come from, the used→remaining conversion, and your conclusion about the NumberBox overlay bug.
