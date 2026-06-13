namespace FluentAgentBar;

internal sealed record TaskbarTarget(IntPtr Hwnd, bool IsPrimary);

internal sealed record TaskbarWidgetReconciliationPlan(
    IReadOnlyList<TaskbarTarget> Create,
    IReadOnlyList<IntPtr> Close,
    IReadOnlyList<IntPtr> Keep);

internal static class TaskbarWidgetReconciler
{
    internal static TaskbarWidgetReconciliationPlan BuildPlan(
        IEnumerable<IntPtr> existingWidgetTaskbars,
        IEnumerable<TaskbarTarget> currentTaskbars)
    {
        List<IntPtr> existing = DistinctNonZero(existingWidgetTaskbars);
        List<TaskbarTarget> current = DistinctNonZero(currentTaskbars);

        HashSet<IntPtr> existingSet = new(existing);
        HashSet<IntPtr> currentSet = new(current.Select(target => target.Hwnd));

        List<IntPtr> keep = existing.Where(currentSet.Contains).ToList();
        List<IntPtr> close = existing.Where(hwnd => !currentSet.Contains(hwnd)).ToList();
        List<TaskbarTarget> create = current
            .Where(target => !existingSet.Contains(target.Hwnd))
            .ToList();

        return new TaskbarWidgetReconciliationPlan(create, close, keep);
    }

    private static List<IntPtr> DistinctNonZero(IEnumerable<IntPtr> hwnds)
    {
        List<IntPtr> result = [];
        HashSet<IntPtr> seen = [];

        foreach (IntPtr hwnd in hwnds)
        {
            if (hwnd != IntPtr.Zero && seen.Add(hwnd))
            {
                result.Add(hwnd);
            }
        }

        return result;
    }

    private static List<TaskbarTarget> DistinctNonZero(IEnumerable<TaskbarTarget> targets)
    {
        List<TaskbarTarget> result = [];
        HashSet<IntPtr> seen = [];

        foreach (TaskbarTarget target in targets)
        {
            if (target.Hwnd != IntPtr.Zero && seen.Add(target.Hwnd))
            {
                result.Add(target);
            }
        }

        return result;
    }
}
