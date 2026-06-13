using Xunit;

namespace FluentAgentBar.Tests;

public sealed class TaskbarWidgetReconcilerTests
{
    [Fact]
    public void BuildPlan_CreatesCurrentTaskbarsInStableOrder()
    {
        TaskbarWidgetReconciliationPlan plan = TaskbarWidgetReconciler.BuildPlan(
            [],
            [
                new TaskbarTarget(new IntPtr(10), IsPrimary: true),
                new TaskbarTarget(new IntPtr(20), IsPrimary: false),
                new TaskbarTarget(new IntPtr(30), IsPrimary: false)
            ]);

        Assert.Equal([new IntPtr(10), new IntPtr(20), new IntPtr(30)], plan.Create.Select(target => target.Hwnd));
        Assert.True(plan.Create[0].IsPrimary);
        Assert.False(plan.Create[1].IsPrimary);
        Assert.Empty(plan.Close);
        Assert.Empty(plan.Keep);
    }

    [Fact]
    public void BuildPlan_KeepsExistingAndClosesMissingTaskbars()
    {
        TaskbarWidgetReconciliationPlan plan = TaskbarWidgetReconciler.BuildPlan(
            [new IntPtr(10), new IntPtr(20), new IntPtr(40)],
            [
                new TaskbarTarget(new IntPtr(20), IsPrimary: true),
                new TaskbarTarget(new IntPtr(30), IsPrimary: false)
            ]);

        Assert.Equal([new IntPtr(20)], plan.Keep);
        Assert.Equal([new IntPtr(10), new IntPtr(40)], plan.Close);
        Assert.Equal([new IntPtr(30)], plan.Create.Select(target => target.Hwnd));
    }

    [Fact]
    public void BuildPlan_FiltersZeroAndDuplicateHandles()
    {
        TaskbarWidgetReconciliationPlan plan = TaskbarWidgetReconciler.BuildPlan(
            [IntPtr.Zero, new IntPtr(10), new IntPtr(10), new IntPtr(20)],
            [
                new TaskbarTarget(IntPtr.Zero, IsPrimary: true),
                new TaskbarTarget(new IntPtr(10), IsPrimary: true),
                new TaskbarTarget(new IntPtr(10), IsPrimary: false),
                new TaskbarTarget(new IntPtr(30), IsPrimary: false),
                new TaskbarTarget(new IntPtr(30), IsPrimary: false)
            ]);

        Assert.Equal([new IntPtr(10)], plan.Keep);
        Assert.Equal([new IntPtr(20)], plan.Close);
        Assert.Equal([new IntPtr(30)], plan.Create.Select(target => target.Hwnd));
    }
}
