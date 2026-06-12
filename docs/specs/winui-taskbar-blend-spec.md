# Spec: Make the WinUI taskbar widget blend into the taskbar (FluentFlyout pattern)

Repo: `e:\Tools\Codex-SWBar-Windows`, project `winui\CodexSwbarWinUI.csproj` (WinUI 3 / WinAppSDK, net8.0-windows10.0.19041.0, x64).
Build check: `dotnet build winui\CodexSwbarWinUI.csproj -c Debug -p:Platform=x64` must end with 0 errors / 0 warnings.

## Problem

`TaskbarWidgetWindow` is currently reparented as a `WS_CHILD` of `Shell_TrayWnd` and paints an opaque pill whose color is *sampled from screen pixels* (`TrySampleTaskbarColor`, `UpdateTaskbarMaterial`, `Blend`, `Luminance` in `TaskbarWidgetWindow.xaml.cs`). The result is an opaque blob that does not match the taskbar's acrylic material. FluentFlyout (github.com/unchihugo/FluentFlyout) instead uses a **top-level borderless window with a fully transparent background**, positioned over the taskbar: only the content pixels are drawn, the taskbar material shows through everywhere else.

## Task 1 — Transparent widget window

In `winui/`:

1. Create `TransparentBackdrop.cs`:

```csharp
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CodexSwbarWinUI;

internal sealed partial class TransparentBackdrop : SystemBackdrop
{
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        connectedTarget.SystemBackdrop = CompositionTarget.GetCompositorForCurrentThread()
            .CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
```

(`CompositionTarget.GetCompositorForCurrentThread()` is `Microsoft.UI.Xaml.Media.CompositionTarget`. If the exact API differs on this WinAppSDK version, adapt — the goal is a transparent composition color brush as system backdrop.)

2. `TaskbarWidgetWindow.xaml.cs`:
   - In the constructor set `SystemBackdrop = new TransparentBackdrop();`.
   - **Remove the reparenting**: delete the `SetParent` call and the `WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN` style juggling in `PositionInTaskbar`. The window stays a top-level borderless popup.
   - Position it in **screen coordinates** over the taskbar at the same logical spot as today: left of the tray (`TryGetTrayRect`), vertically centered in the taskbar band. So `x = taskbarRect.Left + (previous relative x)` etc. Keep the vertical-taskbar branch working with the same translation.
   - Show it with `HWND_TOPMOST` instead of `HWND_TOP`, keep `SWP_NOACTIVATE`, keep `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` and removal of `WS_EX_APPWINDOW`.
   - Delete `SetRoundedRegion` usage for this window (nothing to clip anymore), and delete the entire pixel-sampling machinery: `UpdateTaskbarMaterial`, `TrySampleTaskbarColor`, `ColorFromColorRef`, `Blend`, `Mix`, `Luminance`, and the `WidgetSurfaceBrush` / `WidgetBorderBrush` properties.
   - Replace `_darkTaskbar` detection with the registry value `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize`, DWORD `SystemUsesLightTheme` (0 = dark taskbar). Read it in `RefreshConfigBindings` (cheap enough). Keep `ApplyStatusBrushes`/`StatusFillColor` exactly as they are, driven by that flag.
   - Track/text brushes by theme:
     - dark taskbar: `TrackBrush` = #30FFFFFF, `TextPrimaryBrush` = #FFFFFFFF, `TextSecondaryBrush` = #C6FFFFFF
     - light taskbar: `TrackBrush` = #28000000, `TextPrimaryBrush` = #E4000000, `TextSecondaryBrush` = #9E000000
   - Keep the click-polling timer (`OnClickTimerTick`) and `UsageRequested` event untouched.

3. `TaskbarWidgetWindow.xaml`:
   - Remove the outer pill `Border` entirely (no background, no border, no corner radius). The two-bar grid sits directly in the transparent `Shell` grid with `Padding`-equivalent margins (`Margin="10,5"` on the inner grid). The widget must look like native tray content (clock/weather style): just labels + bars + percents floating on the taskbar.
   - Keep the existing two-row structure exactly (5h / Wk labels, GridLength star fill bars, percent texts), bound to the brushes above.

## Task 2 — Thin acrylic flyout

1. Create `ThinAcrylicBackdrop.cs` — a `SystemBackdrop` subclass using `Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController` with `Kind = DesktopAcrylicKind.Thin`, wired with `GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot)`, `AddSystemBackdropTarget`, and proper disposal in `OnTargetDisconnected`. This matches Windows 11 taskbar flyouts (network/volume).
2. `FlyoutWindow.xaml.cs`: replace `SystemBackdrop = new DesktopAcrylicBackdrop();` with `new ThinAcrylicBackdrop()`.
3. `FlyoutWindow.xaml`: on the outer `Border` keep the 1px `SurfaceStrokeColorDefaultBrush` stroke and `CornerRadius="12"`, but make sure nothing opaque covers the acrylic: the provider cards keep `CardBackgroundFillColorDefaultBrush` (that one is fine, it is translucent by design). Do not add any new solid background.

## Task 3 — Settings: flatten the General section

In `SettingsWindow.xaml`, replace the `toolkit:SettingsExpander` ("General") with the same flat pattern used by the Profiles section below it: a `StackPanel Spacing="8"` containing a small header block (`TextBlock "General"` with `BodyStrongTextBlockStyle` + `TextBlock "Refresh and surface preferences"` with `BodyTextBlockStyle`) followed by the three existing `toolkit:SettingsCard`s (`RefreshIntervalCard`, `ClaudeCard`, `BackdropCard`) as direct children, full width, not nested in expander items. Keep all `x:Name`s and the cards' content controls unchanged. Check `SettingsWindow.xaml.cs` for references to `GeneralExpander` and remove/adjust them if any.

## Constraints

- Follow `AGENTS.md` and `docs/design/fluent-windows.md` (Fluent 2, subtle 1px strokes, no new gradients, spacing on the 4/8/12/16 grid).
- Do not touch `src/main.cpp` (native app) or anything outside `winui/` except nothing.
- Do not commit. Leave changes in the working tree.
- Verify the build compiles with 0 warnings before finishing.
