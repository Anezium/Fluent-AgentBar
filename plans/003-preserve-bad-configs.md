# Plan 003: Preserve bad configs instead of replacing them silently

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the next
> step. If anything in the "STOP conditions" section occurs, stop and report.
> When done, update the status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**:
> `git diff --stat d48750f..HEAD -- winui/AppConfigStore.cs winui/FlyoutWindow.xaml.cs winui/TaskbarWidgetWindow.xaml.cs winui/SettingsWindow.xaml.cs winui/FluentAgentBar.Tests`
> If any in-scope file changed since this plan was written, compare the current
> state excerpts below against the live code before proceeding.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: `plans/001-winui-verification-baseline.md`
- **Category**: bug, dx
- **Planned at**: commit `d48750f`, 2026-06-13

## Why this matters

`AppConfigStore.Load()` currently swallows read/parse errors and returns a
default config. Several UI actions save `AppConfigStore.Load()` to make sure the
config file exists before opening it. If the existing config is malformed but
recoverable by a user, those actions can replace it with defaults and hide the
real problem.

## Current state

- `winui/AppConfigStore.cs:95` defines `Load()`.
- `winui/AppConfigStore.cs:111-115` catches all exceptions and returns
  `Normalize(CreateDefaultConfig())`.
- `winui/AppConfigStore.cs:139` writes config with `File.WriteAllText`.
- `winui/FlyoutWindow.xaml.cs:550` calls `AppConfigStore.Save(AppConfigStore.Load())`
  before opening the config file.
- `winui/TaskbarWidgetWindow.xaml.cs:516` does the same from the context menu.

Repo conventions:

- Keep config JSON camelCase and indented through the existing
  `JsonSerializerOptions`.
- Non-fatal errors are logged defensively; do not surface raw exception stacks
  in the flyout.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Test | `dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug` | exit 0 |
| Verify | `.\scripts\verify-winui.ps1` | exit 0 |

## Scope

**In scope**:

- `winui/AppConfigStore.cs`
- `winui/FlyoutWindow.xaml.cs`
- `winui/TaskbarWidgetWindow.xaml.cs`
- `winui/SettingsWindow.xaml.cs` only if needed for user-visible error text
- `winui/FluentAgentBar.Tests/`

**Out of scope**:

- Changing the config schema.
- Changing profile behavior.
- Reading real user credential folders in tests.

## Git workflow

- Branch: `advisor/003-preserve-bad-configs`
- Commit message: `Preserve invalid config files`
- Do not push or open a PR unless the operator instructed it.

## Steps

### Step 1: Add a load result that distinguishes default from parse failure

Extend `AppConfigStore` with a small internal result type, for example:

```csharp
internal sealed record AppConfigLoadResult(AppConfig Config, bool UsedFallback, string? ErrorMessage);
```

Add `LoadWithStatus()` that:

- Copies legacy config if needed, same as `Load()`.
- Returns `UsedFallback=false` when an existing config reads and normalizes.
- Returns `UsedFallback=true` with a safe error message when the config exists
  but cannot be parsed/read.
- Returns `UsedFallback=false` for a first-run default when no config exists.

Keep `Load()` as a compatibility wrapper returning `LoadWithStatus().Config`.

**Verify**:
`dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug`
still exits 0.

### Step 2: Backup invalid config before any fallback save

Add a helper such as `BackupInvalidConfig(string reason)` that copies
`config.json` to a timestamped sibling like:

```text
config.invalid-20260613-190000.json
```

Do not include exception stack traces in the filename or JSON. If copying fails,
log through `Debug.WriteLine` and continue without deleting the original.

Only overwrite `config.json` with defaults after a backup has been attempted.

**Verify**:
Add tests with a temporary config directory if needed. If the static
`ConfigDirectory` blocks testing, introduce an internal path provider or
overload limited to tests rather than using real `%APPDATA%`.

### Step 3: Stop open-config actions from overwriting invalid config

Change the open config actions in `FlyoutWindow.xaml.cs` and
`TaskbarWidgetWindow.xaml.cs`:

- If `config.json` does not exist, save a default config to create it.
- If `config.json` exists, open it directly.
- Do not call `Save(Load())` unconditionally.

This preserves malformed files so a user can edit them.

**Verify**:
`rg -n "Save\\(AppConfigStore\\.Load\\(\\)\\)" winui` returns no matches.

### Step 4: Add tests for bad config preservation

Add tests covering:

- Invalid JSON returns a fallback config with `UsedFallback=true`.
- Existing invalid JSON is not replaced simply by the "open config" helper. If
  the open action is hard to test directly, extract a small internal helper from
  the duplicated open-config code and test that helper.
- Missing config still creates a valid default when explicitly requested.

**Verify**:
`dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug`
exits 0.

### Step 5: Run full verification

**Verify**:
`.\scripts\verify-winui.ps1` exits 0.

## Test plan

- Unit tests live under `winui/FluentAgentBar.Tests/`.
- Use temporary directories and never use the real AppData config path.
- Cover invalid JSON, missing config, and open-config creation behavior.

## Done criteria

- [ ] Invalid existing config is not silently overwritten by open-config actions.
- [ ] `Load()` remains source-compatible for existing callers.
- [ ] Tests cover invalid config preservation.
- [ ] `rg -n "Save\\(AppConfigStore\\.Load\\(\\)\\)" winui` returns no matches.
- [ ] `.\scripts\verify-winui.ps1` exits 0.
- [ ] `plans/README.md` status row updated.

## STOP conditions

Stop and report if:

- Testing requires changing production config paths in a way that could affect
  real user data.
- The desired backup behavior conflicts with an existing migration flow not
  captured here.
- A step requires changing the config schema.

## Maintenance notes

Any future config migration should preserve the same invariant: user-authored
config is never overwritten after a parse/read failure without preserving the
original bytes first.
