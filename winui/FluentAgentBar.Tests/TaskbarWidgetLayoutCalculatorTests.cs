using FluentAgentBar.WpfTaskbarWidget;
using Xunit;

namespace FluentAgentBar.Tests;

public sealed class TaskbarWidgetLayoutCalculatorTests
{
    [Fact]
    public void Calculate_HorizontalTaskbar_PlacesPillLeftOfTrayAndCentersVertically()
    {
        NativeRect taskbar = new(0, 1040, 1920, 1080);
        NativeRect tray = new(1700, 1040, 1920, 1080);

        TaskbarWidgetLayout layout = TaskbarWidgetLayoutCalculator.Calculate(taskbar, tray, 96);

        Assert.True(layout.IsHorizontal);
        Assert.Equal(new NativeRect(1388, 1040, 1692, 1080), layout.PillScreenRect);
        Assert.Equal(new NativeRect(1388, 0, 1692, 40), layout.PillRegionRect);
        Assert.Equal(8, tray.Left - layout.PillScreenRect.Right);
        Assert.True(TaskbarWidgetLayoutCalculator.CanHostAsChild(layout));
    }

    [Fact]
    public void Calculate_PerTaskbarDpi_ScalesSizeGapAndRegion()
    {
        NativeRect taskbar = new(3840, 2088, 7680, 2160);
        NativeRect tray = new(7200, 2088, 7680, 2160);

        TaskbarWidgetLayout layout = TaskbarWidgetLayoutCalculator.Calculate(taskbar, tray, 144);

        Assert.Equal(144u, layout.Dpi);
        Assert.Equal(new NativeRect(6732, 2094, 7188, 2154), layout.PillScreenRect);
        Assert.Equal(new NativeRect(2892, 6, 3348, 66), layout.PillRegionRect);
        Assert.Equal(12, tray.Left - layout.PillScreenRect.Right);
    }

    [Fact]
    public void Calculate_VerticalTaskbar_PreservesLegacyBranchAboveTray()
    {
        NativeRect taskbar = new(0, 0, 48, 1080);
        NativeRect tray = new(0, 800, 48, 1080);

        TaskbarWidgetLayout layout = TaskbarWidgetLayoutCalculator.Calculate(taskbar, tray, 96);

        Assert.False(layout.IsHorizontal);
        Assert.Equal(new NativeRect(0, 752, 304, 792), layout.PillScreenRect);
        Assert.Equal(new NativeRect(0, 752, 304, 792), layout.PillRegionRect);
        Assert.Equal(8, tray.Top - layout.PillScreenRect.Bottom);
        Assert.False(TaskbarWidgetLayoutCalculator.CanHostAsChild(layout));
    }

    [Fact]
    public void Calculate_ZeroDpi_UsesNinetySixDpi()
    {
        TaskbarWidgetLayout layout = TaskbarWidgetLayoutCalculator.Calculate(
            new NativeRect(100, 500, 1100, 540),
            new NativeRect(900, 500, 1100, 540),
            0);

        Assert.Equal(96u, layout.Dpi);
        Assert.Equal(304, layout.PillScreenRect.Width);
        Assert.Equal(40, layout.PillScreenRect.Height);
    }

    [Fact]
    public void Calculate_EmptyTaskbar_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TaskbarWidgetLayoutCalculator.Calculate(default, default, 96));
    }
}
