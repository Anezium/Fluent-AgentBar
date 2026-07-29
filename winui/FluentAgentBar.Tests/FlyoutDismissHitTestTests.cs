using Xunit;

namespace FluentAgentBar.Tests;

public sealed class FlyoutDismissHitTestTests
{
    private static readonly NativeMethods.Rect FlyoutRect = new()
    {
        Left = 100,
        Top = 100,
        Right = 500,
        Bottom = 700
    };

    private static readonly NativeMethods.Rect WidgetRect = new()
    {
        Left = 300,
        Top = 720,
        Right = 500,
        Bottom = 760
    };

    [Fact]
    public void ShouldDismiss_ClickInsideFlyout_ReturnsFalse()
    {
        NativeMethods.Point cursor = new() { X = 250, Y = 300 };

        bool result = FlyoutDismissHitTest.ShouldDismiss(
            cursor,
            FlyoutRect,
            hasWidgetAnchor: true,
            WidgetRect);

        Assert.False(result);
    }

    [Fact]
    public void ShouldDismiss_ClickOnAnchoredWidget_ReturnsFalse()
    {
        NativeMethods.Point cursor = new() { X = 400, Y = 740 };

        bool result = FlyoutDismissHitTest.ShouldDismiss(
            cursor,
            FlyoutRect,
            hasWidgetAnchor: true,
            WidgetRect);

        Assert.False(result);
    }

    [Fact]
    public void ShouldDismiss_ClickOutsideBothSurfaces_ReturnsTrue()
    {
        NativeMethods.Point cursor = new() { X = 50, Y = 50 };

        bool result = FlyoutDismissHitTest.ShouldDismiss(
            cursor,
            FlyoutRect,
            hasWidgetAnchor: true,
            WidgetRect);

        Assert.True(result);
    }

    [Fact]
    public void ShouldDismiss_ClickOnWidgetWithoutAnchor_ReturnsTrue()
    {
        NativeMethods.Point cursor = new() { X = 400, Y = 740 };

        bool result = FlyoutDismissHitTest.ShouldDismiss(
            cursor,
            FlyoutRect,
            hasWidgetAnchor: false,
            WidgetRect);

        Assert.True(result);
    }
}
