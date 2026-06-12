# Spec: widget stability + flyout visual polish

Repo: `e:\Tools\Codex-SWBar-Windows`, project `winui\CodexSwbarWinUI.csproj` (WinUI 3, net8.0-windows10.0.19041.0, x64).
Build check: `dotnet build winui/CodexSwbarWinUI.csproj -c Debug -p:Platform=x64` → must end 0 errors / 0 warnings.
Do not commit. Work only inside `winui/`.

## Context — fragile code, do NOT refactor

`TaskbarWidgetWindow` was just converted to a transparent top-level overlay over the taskbar. The current sequence in `TaskbarWidgetWindow.xaml.cs` is **load-bearing and empirically tuned**; keep it exactly:

- `TransparentBackdrop` + `DwmExtendFrameIntoClientArea(-1)` + `DWMWA_BORDER_COLOR = NONE` happen in the constructor (`ConfigureNativeWindow`). Moving the frame extension after the first show breaks XAML island rendering (window shows nothing).
- `WS_CAPTION` strip MUST stay deferred via `_frameFixTimer` (200 ms one-shot started in `PositionInTaskbar`): stripping it before the first presented frame also breaks rendering permanently.
- Do not reintroduce `SetParent`/`WS_CHILD`, pixel sampling, or an opaque pill background.

## Task 1 — Widget must never disappear

Reported bug: the widget vanishes from the taskbar after a while ("elle se barre"). It is a `WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE` topmost window; likely causes: other topmost windows / shell re-asserts z-order, explorer restarts, display/DPI changes, or fullscreen apps toggling.

Implement a watchdog in `TaskbarWidgetWindow`:

1. Reuse the existing 40 ms `_clickTimer` tick: every ~50 ticks (≈2 s), verify:
   - `NativeMethods.IsWindowVisible(_hwnd)` is true, and
   - the widget rect still matches the expected spot next to the tray (recompute the target x/y like `PositionInTaskbar` does; tolerance of a few px), and
   - the window is still above the taskbar in z-order.
   If any check fails, call the existing `PositionInTaskbar()` again (it is idempotent; `_frameFixTimer` restart is fine since the strip is a no-op once applied).
2. Exception: while a fullscreen app covers the monitor (use `SHQueryUserNotificationState` or compare foreground window rect to monitor rect), do NOT force the widget topmost — skip the re-assert for that tick so games/videos are not overdrawn. Add the P/Invoke to `NativeMethods` following the existing style (`LibraryImport`/`DllImport` patterns).
3. Handle explorer restart: if `FindPrimaryTaskbar()` returns a different HWND than last time, reposition.

Keep all added code small and in the existing style (file-scoped namespace, explicit braces).

## Task 2 — Flyout visual pass (make it feel like a Windows 11 Quick Settings flyout)

`FlyoutWindow` already uses `ThinAcrylicBackdrop` (keep it). Fix the layout in `FlyoutWindow.xaml`:

1. **Remove the double border**: the outer `Border` currently has `BorderBrush={ThemeResource SurfaceStrokeColorDefaultBrush}, BorderThickness=1, CornerRadius=12` — DWM already draws the window outline and rounded corners. Remove the XAML BorderBrush/BorderThickness/CornerRadius (keep `Padding="16"`).
2. **Remove the letter badges**: delete the 28×28 "C"/"A" circle `Border`s in the provider card header; the provider `Name` (BodyStrong) + `ProfileCountText` (Caption, `TextFillColorSecondaryBrush`) are enough. Keep alignment clean.
3. **Size to content**: content currently overflows 420×420 (the Claude card is cut). Change `FlyoutLogicalWidth` to 400 and `FlyoutLogicalHeight` to 520 in `FlyoutWindow.xaml.cs`, and the `Shell` grid `Width`/`Height` in the XAML to match.
4. **Bars**: in the profile template, change the two `ProgressBar`s from `Height="4"` to `MinHeight="5" Height="5"` and give both labels the same fixed label column (currently 48px, keep) with texts `5h` and `Weekly`. Percent column 38px right-aligned Caption SemiBold (already there — keep).
5. **Provider cards**: keep `CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` 1px + `CornerRadius=8`, reduce card `Padding` from 16 to 14,12 and inner `StackPanel Spacing` from 12 to 10 so the flyout is denser (Fluent “dense but breathable”).
6. **Footer**: keep the three buttons; set their height to 36 and keep 8px gaps.
7. Re-check against `docs/design/fluent-windows.md` checklist (subtle strokes, spacing scale, no gradients).

## Verification

Build only (0 errors / 0 warnings). Do not launch the app; the maintainer will verify visually.
