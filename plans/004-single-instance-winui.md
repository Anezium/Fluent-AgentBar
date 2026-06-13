# Plan 004: Enforce single-instance WinUI startup

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the next
> step. If anything in the "STOP conditions" section occurs, stop and report.
> When done, update the status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**:
> `git diff --stat d48750f..HEAD -- README.md winui/App.xaml.cs winui/NativeMethods.cs winui/FluentAgentBar.Tests`
> If any in-scope file changed since this plan was written, compare the current
> state excerpts below against the live code before proceeding.

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: MED
- **Depends on**: `plans/001-winui-verification-baseline.md`
- **Category**: bug
- **Planned at**: commit `d48750f`, 2026-06-13

## Why this matters

The README promises that launching the executable again focuses the existing
Settings window. The active WinUI app currently creates a new `UsageService`,
widget, and flyout on every launch. Multiple instances can create overlapping
taskbar widgets, duplicate refresh loops, and confusing settings windows.

## Current state

- `README.md:35` says launching the executable again focuses the existing
  instance's Settings window.
- `winui/App.xaml.cs:22` starts the app in `OnLaunched` without an app-instance
  guard.
- `winui/App.xaml.cs:24-30` creates `UsageService`, widget, flyout, and starts
  the refresh loop.
- `winui/App.xaml.cs:33` handles `--show-settings`, but no second-process
  redirect exists.
- Legacy C++ used a mutex at `src/main.cpp:5552`; do not port by depending on
  the C++ code. Implement this in WinUI/.NET.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Test | `dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug` | exit 0 |
| Verify | `.\scripts\verify-winui.ps1` | exit 0 |
| Manual smoke | Launch exe twice from PowerShell | second launch focuses settings; only one process remains active |

## Scope

**In scope**:

- `winui/App.xaml.cs`
- Optional new file: `winui/SingleInstanceService.cs`
- Optional P/Invoke additions in `winui/NativeMethods.cs`
- `README.md`
- `winui/FluentAgentBar.Tests/` for testable argument/decision logic

**Out of scope**:

- Multi-taskbar support.
- Rewriting app startup architecture beyond what single-instance needs.
- Any dependency on `src/main.cpp`.

## Git workflow

- Branch: `advisor/004-single-instance-winui`
- Commit message: `Enforce single-instance startup`
- Do not push or open a PR unless the operator instructed it.

## Steps

### Step 1: Choose the WinUI-native single-instance mechanism

Prefer Windows App SDK `AppInstance` APIs if they work for unpackaged WinUI with
`WindowsPackageType=None`. If not, use a named mutex plus a small IPC or window
message path implemented in WinUI.

Document the choice in a short comment near the implementation. Do not use the
legacy C++ mutex name; use a new product name such as
`FluentAgentBar.SingleInstance`.

**Verify**:
`dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64` exits 0.

### Step 2: Gate startup before creating windows/services

Move the single-instance check to the earliest practical point in
`App.OnLaunched`, before:

- `new UsageService()`
- `CreateTaskbarWidget()`
- `new FlyoutWindow(...)`
- `_usageService.Start()`

If this process is not primary, signal the primary instance to show Settings and
exit without creating widgets or starting refresh loops.

**Verify**:
Add or update tests for a pure decision helper if one is introduced, then run
`dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug`.

### Step 3: Route activation requests to the existing instance

Support these activation intents:

- Default second launch: show Settings, matching README.
- `--show-settings`: show Settings.
- `--show-flyout`: show flyout if possible.

Keep implementation small. If using IPC, pass only the command intent, not
environment details or file paths.

**Verify**:
Manual smoke:

```powershell
dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64
.\winui\bin\Debug\net8.0-windows10.0.19041.0\win-x64\FluentAgentBar.exe
.\winui\bin\Debug\net8.0-windows10.0.19041.0\win-x64\FluentAgentBar.exe
```

Expected: the second launch exits quickly and the first instance shows Settings.
Use Task Manager or PowerShell to confirm only one active `FluentAgentBar`
process remains.

### Step 4: Update docs

Keep README wording accurate. If the second launch shows Settings by design,
leave the current promise and add a short note that command-line flags can show
Settings or the flyout.

**Verify**:
`rg -n "second launch|existing instance|--show-settings|--show-flyout" README.md`
shows accurate wording.

### Step 5: Run full verification

**Verify**:
`.\scripts\verify-winui.ps1` exits 0.

## Test plan

- Unit-test any pure activation-intent parser or single-instance decision logic.
- Manual smoke test is required because process-instance behavior is OS-level.

## Done criteria

- [ ] Second launch no longer creates a second widget or refresh loop.
- [ ] Default second launch focuses/shows Settings.
- [ ] `--show-settings` and `--show-flyout` route to the existing instance.
- [ ] README matches behavior.
- [ ] `.\scripts\verify-winui.ps1` exits 0.
- [ ] `plans/README.md` status row updated.

## STOP conditions

Stop and report if:

- Windows App SDK `AppInstance` is unavailable for this unpackaged app and IPC
  would require a broad startup rewrite.
- Manual smoke shows two persistent processes after reasonable fix attempts.
- You need to change multi-taskbar logic to make single instance work.

## Maintenance notes

Multi-taskbar work should assume a single process owns all widgets. Reviewers
should look carefully for startup paths that create windows before the
single-instance guard runs.
