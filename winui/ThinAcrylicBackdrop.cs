using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentAgentBar;

internal sealed partial class ThinAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        SystemDispatcherQueue.Ensure();
        _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin
        };
        if (_configuration.Theme == SystemBackdropTheme.Dark)
        {
            // Pull the acrylic towards the Windows 11 taskbar tone so the
            // flyout reads as part of the shell rather than a grey app window.
            _controller.TintColor = Windows.UI.Color.FromArgb(255, 32, 32, 36);
            _controller.TintOpacity = 0.65f;
            _controller.LuminosityOpacity = 0.9f;
            _controller.FallbackColor = Windows.UI.Color.FromArgb(255, 32, 32, 36);
        }
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        _configuration = null;

        base.OnTargetDisconnected(disconnectedTarget);
    }
}
