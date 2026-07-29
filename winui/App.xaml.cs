using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FluentAgentBar;

public partial class App : Application
{
    private SingleInstanceService? _singleInstanceService;
    private UsageService? _usageService;
    private FlyoutWindow? _flyoutWindow;
    private readonly Dictionary<IntPtr, TaskbarWidgetHost> _taskbarWidgetWindows = [];
    private DispatcherQueueTimer? _widgetRecoveryTimer;
    private DispatcherQueueTimer? _taskbarReconcileTimer;
    private DispatcherQueue? _dispatcherQueue;
    private int _taskbarSettleTicks;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        SingleInstanceStartupResult startup = await SingleInstanceService.RegisterOrRedirectAsync(
            _dispatcherQueue,
            HandleActivationIntent);
        if (!startup.IsPrimary)
        {
            Exit();
            return;
        }

        _singleInstanceService = startup.Service;
        _usageService = new UsageService();

        ReconcileTaskbarWidgets();
        StartTaskbarReconcileTimer();

        _flyoutWindow = new FlyoutWindow(_usageService);
        _flyoutWindow.PrepareHidden();
        _usageService.Start();

        ActivationIntent? initialIntent = SingleInstanceService.ParseExplicitActivationIntent(
            Environment.GetCommandLineArgs().Skip(1));
        if (initialIntent is { } intent)
        {
            HandleActivationIntent(intent);
        }
    }

    private void ReconcileTaskbarWidgets()
    {
        if (_exiting || _usageService is null)
        {
            return;
        }

        IReadOnlyList<TaskbarTarget> currentTaskbars = NativeMethods.FindTaskbars();
        TaskbarWidgetReconciliationPlan plan = TaskbarWidgetReconciler.BuildPlan(
            _taskbarWidgetWindows.Keys,
            currentTaskbars);

        foreach (IntPtr hwnd in plan.Close)
        {
            if (_taskbarWidgetWindows.Remove(hwnd, out TaskbarWidgetHost? widget))
            {
                UnsubscribeTaskbarWidget(widget);
                widget.Close();
            }
        }

        foreach (TaskbarTarget target in plan.Create)
        {
            TaskbarWidgetHost? widget = null;
            try
            {
                widget = new TaskbarWidgetHost(_usageService, target);
                SubscribeTaskbarWidget(widget);
                _taskbarWidgetWindows[target.Hwnd] = widget;
                widget.ShowNoActivate();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                _taskbarWidgetWindows.Remove(target.Hwnd);
                if (widget is not null)
                {
                    UnsubscribeTaskbarWidget(widget);
                    widget.Close();
                }
            }
        }

        if (currentTaskbars.Count == 0 || _taskbarWidgetWindows.Count == 0)
        {
            StartWidgetRecoveryTimer();
        }
        else
        {
            StopWidgetRecoveryTimer();
        }
    }

    private void SubscribeTaskbarWidget(TaskbarWidgetHost widget)
    {
        widget.UsageRequested += OnTaskbarUsageRequested;
        widget.ExitRequested += OnExitRequested;
        widget.TargetLost += OnTaskbarTargetLost;
        widget.Closed += OnTaskbarWidgetClosed;
    }

    private void UnsubscribeTaskbarWidget(TaskbarWidgetHost widget)
    {
        widget.UsageRequested -= OnTaskbarUsageRequested;
        widget.ExitRequested -= OnExitRequested;
        widget.TargetLost -= OnTaskbarTargetLost;
        widget.Closed -= OnTaskbarWidgetClosed;
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        StopTaskbarReconcileTimer();
        StopWidgetRecoveryTimer();
        _usageService?.Dispose();
        SettingsWindow.CloseInstance();
        _flyoutWindow?.Close();
        CloseTaskbarWidgets();
        Exit();
    }

    // Explorer restarts and monitor changes can remove or replace taskbar HWNDs.
    // Wait for the shell to settle, then reconcile without creating duplicates.
    private void OnTaskbarWidgetClosed(object? sender, EventArgs args)
    {
        if (sender is TaskbarWidgetHost widget)
        {
            RemoveTaskbarWidget(widget);
            // Close() is idempotent; on the unexpected-close path nobody else
            // stops the host's cycle timer or detaches its static-event
            // subscriptions, so the host would otherwise leak and keep ticking.
            widget.Close();
        }

        if (_exiting)
        {
            return;
        }

        StartWidgetRecoveryTimer();
    }

    private void OnTaskbarTargetLost(object? sender, EventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        if (sender is TaskbarWidgetHost widget)
        {
            RemoveTaskbarWidget(widget);
            widget.Close();
        }

        ReconcileTaskbarWidgets();
    }

    private void StartWidgetRecoveryTimer()
    {
        if (_exiting || _widgetRecoveryTimer is not null)
        {
            return;
        }

        _taskbarSettleTicks = 0;
        _widgetRecoveryTimer = (_dispatcherQueue ?? DispatcherQueue.GetForCurrentThread()).CreateTimer();
        _widgetRecoveryTimer.Interval = TimeSpan.FromSeconds(1);
        _widgetRecoveryTimer.Tick += OnWidgetRecoveryTick;
        _widgetRecoveryTimer.Start();
    }

    private void StopWidgetRecoveryTimer()
    {
        if (_widgetRecoveryTimer is null)
        {
            return;
        }

        _widgetRecoveryTimer.Stop();
        _widgetRecoveryTimer.Tick -= OnWidgetRecoveryTick;
        _widgetRecoveryTimer = null;
    }

    private void StartTaskbarReconcileTimer()
    {
        if (_taskbarReconcileTimer is not null)
        {
            return;
        }

        _taskbarReconcileTimer = (_dispatcherQueue ?? DispatcherQueue.GetForCurrentThread()).CreateTimer();
        _taskbarReconcileTimer.Interval = TimeSpan.FromSeconds(5);
        _taskbarReconcileTimer.Tick += OnTaskbarReconcileTick;
        _taskbarReconcileTimer.Start();
    }

    private void StopTaskbarReconcileTimer()
    {
        if (_taskbarReconcileTimer is null)
        {
            return;
        }

        _taskbarReconcileTimer.Stop();
        _taskbarReconcileTimer.Tick -= OnTaskbarReconcileTick;
        _taskbarReconcileTimer = null;
    }

    private void OnTaskbarReconcileTick(DispatcherQueueTimer sender, object args)
    {
        ReconcileTaskbarWidgets();
    }

    private void OnWidgetRecoveryTick(DispatcherQueueTimer sender, object args)
    {
        if (NativeMethods.FindTaskbars().Count == 0)
        {
            _taskbarSettleTicks = 0;
            return;
        }

        if (++_taskbarSettleTicks < 2)
        {
            return;
        }

        sender.Stop();
        sender.Tick -= OnWidgetRecoveryTick;
        _widgetRecoveryTimer = null;
        ReconcileTaskbarWidgets();
    }

    private void OnTaskbarUsageRequested(object? sender, EventArgs e)
    {
        if (_usageService is null)
        {
            _usageService = new UsageService();
            _usageService.Start();
        }

        _flyoutWindow ??= new FlyoutWindow(_usageService);
        if (sender is TaskbarWidgetHost widget &&
            widget.TryGetPillScreenRect(out NativeMethods.Rect pillRect))
        {
            _flyoutWindow.ToggleNoActivate(widget.TargetHwnd, pillRect);
        }
        else
        {
            _flyoutWindow.ToggleNoActivate();
        }
    }

    private void HandleActivationIntent(ActivationIntent intent)
    {
        if (intent == ActivationIntent.ShowFlyout)
        {
            _flyoutWindow?.ShowNoActivate();
            return;
        }

        SettingsWindow.ShowInstance();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine(e.Exception);
        try
        {
            Directory.CreateDirectory(AppConfigStore.ConfigDirectory);
            File.AppendAllText(
                Path.Combine(AppConfigStore.ConfigDirectory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        _singleInstanceService?.Dispose();
        _usageService?.Dispose();
    }

    private void RemoveTaskbarWidget(TaskbarWidgetHost widget)
    {
        UnsubscribeTaskbarWidget(widget);
        if (!_taskbarWidgetWindows.Remove(widget.TargetHwnd))
        {
            IntPtr keyToRemove = IntPtr.Zero;
            foreach (KeyValuePair<IntPtr, TaskbarWidgetHost> pair in _taskbarWidgetWindows)
            {
                if (ReferenceEquals(pair.Value, widget))
                {
                    keyToRemove = pair.Key;
                    break;
                }
            }

            if (keyToRemove != IntPtr.Zero)
            {
                _taskbarWidgetWindows.Remove(keyToRemove);
            }
        }
    }

    private void CloseTaskbarWidgets()
    {
        foreach (TaskbarWidgetHost widget in _taskbarWidgetWindows.Values.ToList())
        {
            UnsubscribeTaskbarWidget(widget);
            widget.Close();
        }

        _taskbarWidgetWindows.Clear();
    }
}
