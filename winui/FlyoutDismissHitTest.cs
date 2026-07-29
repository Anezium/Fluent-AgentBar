namespace FluentAgentBar;

internal static class FlyoutDismissHitTest
{
    internal static bool ShouldDismiss(
        NativeMethods.Point cursor,
        NativeMethods.Rect flyoutRect,
        bool hasWidgetAnchor,
        NativeMethods.Rect widgetAnchorRect)
    {
        return
            !Contains(flyoutRect, cursor) &&
            (!hasWidgetAnchor || !Contains(widgetAnchorRect, cursor));
    }

    private static bool Contains(NativeMethods.Rect rect, NativeMethods.Point point)
    {
        return
            point.X >= rect.Left &&
            point.X <= rect.Right &&
            point.Y >= rect.Top &&
            point.Y <= rect.Bottom;
    }
}
