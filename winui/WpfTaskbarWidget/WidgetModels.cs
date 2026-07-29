namespace FluentAgentBar.WpfTaskbarWidget;

public readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct TaskbarWidgetLayout(
    NativeRect TaskbarScreenRect,
    NativeRect TrayNotifyScreenRect,
    NativeRect PillScreenRect,
    NativeRect PillRegionRect,
    uint Dpi,
    bool IsHorizontal);

public static class TaskbarWidgetLayoutCalculator
{
    public const int WidgetLogicalWidth = 304;
    public const int WidgetLogicalHeight = 40;
    public const int TrayGapLogical = 8;
    public const int CornerRadiusLogical = 8;

    public static TaskbarWidgetLayout Calculate(
        NativeRect taskbar,
        NativeRect trayNotify,
        uint dpi)
    {
        if (taskbar.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(taskbar), "The taskbar rectangle must have positive dimensions.");
        }

        uint effectiveDpi = dpi == 0 ? 96u : dpi;
        double scale = Math.Max(1.0, effectiveDpi / 96d);
        int widgetWidth = (int)Math.Round(WidgetLogicalWidth * scale);
        int widgetHeight = (int)Math.Round(WidgetLogicalHeight * scale);
        int gap = (int)Math.Round(TrayGapLogical * scale);
        bool horizontal = taskbar.Width >= taskbar.Height;

        int relativeX;
        int relativeY;
        if (horizontal)
        {
            int trayLeft = trayNotify.Left > 0 ? trayNotify.Left : taskbar.Right;
            relativeX = Math.Max(gap, trayLeft - taskbar.Left - widgetWidth - gap);
            relativeY = Math.Max(0, (taskbar.Height - widgetHeight) / 2);
        }
        else
        {
            int trayTop = trayNotify.Top > 0 ? trayNotify.Top : taskbar.Bottom;
            relativeX = Math.Max(0, (taskbar.Width - widgetWidth) / 2);
            relativeY = Math.Max(gap, trayTop - taskbar.Top - widgetHeight - gap);
        }

        NativeRect pill = new(
            taskbar.Left + relativeX,
            taskbar.Top + relativeY,
            taskbar.Left + relativeX + widgetWidth,
            taskbar.Top + relativeY + widgetHeight);
        NativeRect region = new(
            relativeX,
            relativeY,
            relativeX + widgetWidth,
            relativeY + widgetHeight);
        return new TaskbarWidgetLayout(taskbar, trayNotify, pill, region, effectiveDpi, horizontal);
    }

    public static bool CanHostAsChild(TaskbarWidgetLayout layout)
    {
        return
            !layout.TaskbarScreenRect.IsEmpty &&
            layout.PillRegionRect.Left >= 0 &&
            layout.PillRegionRect.Top >= 0 &&
            layout.PillRegionRect.Right <= layout.TaskbarScreenRect.Width &&
            layout.PillRegionRect.Bottom <= layout.TaskbarScreenRect.Height;
    }
}

public sealed record WidgetVisualState(
    string ProviderName,
    string ProfileTitle,
    string ProfilePlan,
    string ProfileLabel,
    string PrimaryQuotaLabel,
    int PrimaryRemainingPercent,
    string PrimaryRemainingText,
    string WeeklyQuotaLabel,
    int WeeklyRemainingPercent,
    string WeeklyRemainingText,
    bool HasWeeklyQuota,
    bool IsDarkTaskbar,
    bool IsGlowEnabled);

public sealed record WidgetMenuEntry(
    string CommandId,
    string Text,
    string? Glyph = null,
    bool IsEnabled = true,
    bool IsChecked = false,
    bool IsSeparator = false,
    IReadOnlyList<WidgetMenuEntry>? Children = null)
{
    public static WidgetMenuEntry Separator() => new(string.Empty, string.Empty, IsSeparator: true);
}

public enum TaskbarWidgetMode
{
    Child,
    OwnedFallback
}

public sealed record TaskbarWidgetRuntimeInfo(
    IntPtr Hwnd,
    IntPtr TaskbarHwnd,
    IntPtr TrayNotifyHwnd,
    TaskbarWidgetLayout Layout,
    TaskbarWidgetMode Mode,
    string? ChildModeFailure);
