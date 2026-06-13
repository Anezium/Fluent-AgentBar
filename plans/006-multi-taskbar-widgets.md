# Plan 006: Support one widget per taskbar monitor

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the next
> step. If anything in the "STOP conditions" section occurs, stop and report.
> When done, update the status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**:
> `git diff --stat d48750f..HEAD -- README.md winui/App.xaml.cs winui/TaskbarWidgetWindow.xaml.cs winui/NativeMethods.cs winui/FluentAgentBar.Tests`
> If any in-scope file changed since this plan was written, compare the current
> state excerpts below against the live code before proceeding.

## Status

- **Priority**: P2
- **Effort**: L
- **Risk**: MED-HIGH
- **Depends on**: `plans/001-winui-verification-baseline.md`, `plans/004-single-instance-winui.md`
- **Category**: bug, direction
- **Planned at**: commit `d48750f`, 2026-06-13

## Why this matters

The README promises one taskbar presence per detected primary/secondary Windows
taskbar. The active WinUI app creates only one widget and all positioning helpers
use the primary `Shell_TrayWnd`. Users with multiple monitors can miss the
widget on secondary taskbars, and future windowing fixes become harder while the
README and implementation disagree.

## Current state

- `README.md:10` promises one taskbar presence per detected primary/secondary
  Windows taskbar.
- `winui/App.xaml.cs:44-50` creates exactly one `TaskbarWidgetWindow`.
- `winui/NativeMethods.cs:167-170` has `FindPrimaryTaskbar()` returning only
  `Shell_TrayWnd`.
- `winui/TaskbarWidgetWindow.xaml.cs:263`, `343`, `389`, and `540` call
  `NativeMethods.FindPrimaryTaskbar()`.
- `winui/NativeMethods.cs:363` recognizes `Shell_SecondaryTrayWnd` only in
  `IsShellWindow`, not for widget placement.
- Do not copy code from `src/main.cpp`; implement the WinUI behavior directly.

Design constraints:

- Keep the transparent WinUI top-level overlay pattern. Do not reintroduce
  `SetParent` or cross-process child windows.
- Preserve `TransparentBackdrop`, `EnableTransparentComposition`, owner
  adoption, and the deferred frame-strip behavior unless a test/manual smoke
  proves a change is necessary.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64` | exit 0 |
| Test | `dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug` | exit 0 |
| Verify | `.\scripts\verify-winui.ps1` | exit 0 |
| Manual smoke | Run app with multiple monitors | one widget per taskbar; no duplicate widgets per taskbar |

## Scope

**In scope**:

- `winui/App.xaml.cs`
- `winui/TaskbarWidgetWindow.xaml.cs`
- `winui/NativeMethods.cs`
- `README.md`
- `winui/FluentAgentBar.Tests/` for pure geometry/enumeration helpers

**Out of scope**:

- Reparenting widgets into Explorer.
- Visual redesign of widget/flyout.
- Supporting non-Windows shells.
- Using `src/main.cpp` as active code.

## Git workflow

- Branch: `advisor/006-multi-taskbar-widgets`
- Commit message: `Support widgets on all taskbars`
- Do not push or open a PR unless the operator instructed it.

## Steps

### Step 1: Model taskbar targets explicitly

Create an internal record such as:

```csharp
internal sealed record TaskbarTarget(IntPtr Hwnd, bool IsPrimary);
```

Add `NativeMethods.FindTaskbars()` that returns the primary `Shell_TrayWnd` plus
all `Shell_SecondaryTrayWnd` windows found by repeated `FindWindowEx`. Preserve
stable ordering: primary first, then secondary taskbars in enumeration order.

**Verify**:
`dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64` exits 0.

### Step 2: Make widget windows target-specific

Change `TaskbarWidgetWindow` to accept a taskbar HWND or `TaskbarTarget` in its
constructor. Replace internal calls to `FindPrimaryTaskbar()` with the target
where possible:

- Size uses target taskbar DPI.
- Position uses target taskbar rect and tray rect.
- Owner adoption uses the target.
- Watchdog compares against the target and detects if that target disappeared.

If the target disappears, raise a signal to `App` so it can rebuild the widget
set from current taskbars.

**Verify**:
`dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64` exits 0.

### Step 3: Manage a widget collection in App

Replace the single `_taskbarWidgetWindow` field with a collection keyed by
taskbar HWND. `CreateTaskbarWidget()` should become something like
`ReconcileTaskbarWidgets()`:

- Enumerate current taskbars.
- Create missing widgets.
- Close/remove widgets whose taskbar no longer exists.
- Subscribe/unsubscribe `UsageRequested`, `ExitRequested`, and `Closed` for
  each widget.

Keep one shared `UsageService` and one shared `FlyoutWindow`.

**Verify**:
`dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64` exits 0.

### Step 4: Update recovery behavior

The current recovery timer waits for `FindPrimaryTaskbar()` after a widget
closes. Generalize it to call `ReconcileTaskbarWidgets()` after Explorer
restart, display changes, or a widget target disappearing.

Do not create duplicate widgets for the same HWND.

**Verify**:
Add tests for any pure reconciliation helper that can be isolated from WinUI
windows. Then run:
`dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug`.

### Step 5: Manual smoke test on Windows

Run the app on a machine with at least two taskbars/monitors:

- Start app: each taskbar gets one widget.
- Left-click any widget: the shared flyout opens.
- Right-click any widget: context menu actions work.
- Restart Explorer: widgets are recreated without duplicates.
- Disconnect/reconnect a monitor: widget set reconciles.

**Verify**:
Record the smoke result in the final summary. If multi-monitor hardware is not
available, explicitly say the manual smoke was not run.

### Step 6: Update README if necessary

If full secondary taskbar support is implemented, keep the current README
promise. If a limitation remains, narrow the promise honestly.

**Verify**:
`rg -n "primary/secondary|taskbar" README.md` matches the implemented behavior.

## Test plan

- Unit-test taskbar enumeration/reconciliation helpers where possible.
- Manual smoke is required for HWND/z-order/Explorer restart behavior.

## Done criteria

- [ ] App owns a collection of taskbar widgets, not a single widget.
- [ ] Primary and secondary taskbars are enumerated.
- [ ] No duplicate widgets are created for the same taskbar HWND.
- [ ] Shared flyout and shared usage service still work.
- [ ] `.\scripts\verify-winui.ps1` exits 0.
- [ ] Manual smoke result is reported.
- [ ] `plans/README.md` status row updated.

## STOP conditions

Stop and report if:

- WinUI transparent composition breaks when multiple top-level widget windows
  are shown.
- Explorer ownership behavior differs for `Shell_SecondaryTrayWnd` and requires
  a new windowing strategy.
- The fix would require reintroducing `SetParent`/`WS_CHILD`.

## Maintenance notes

This code will stay fragile because it interacts with Explorer windows. Keep the
taskbar target model isolated so future fixes can be tested without touching
provider/usage logic.
