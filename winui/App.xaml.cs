using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FluentAgentBar;

public partial class App : Application
{
    private UsageService? _usageService;
    private FlyoutWindow? _flyoutWindow;
    private TaskbarWidgetWindow? _taskbarWidgetWindow;
    private DispatcherQueueTimer? _widgetRecoveryTimer;
    private int _taskbarSettleTicks;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _usageService = new UsageService();

        CreateTaskbarWidget();

        _flyoutWindow = new FlyoutWindow(_usageService);
        _flyoutWindow.PrepareHidden();
        _usageService.Start();

        string[] commandLineArgs = Environment.GetCommandLineArgs();
        if (commandLineArgs.Any(arg => arg.Equals("--show-settings", StringComparison.OrdinalIgnoreCase)))
        {
            SettingsWindow.ShowInstance();
        }

        if (commandLineArgs.Any(arg => arg.Equals("--show-flyout", StringComparison.OrdinalIgnoreCase)))
        {
            _flyoutWindow.ShowNoActivate();
        }
    }

    private void CreateTaskbarWidget()
    {
        _taskbarWidgetWindow = new TaskbarWidgetWindow(_usageService!);
        _taskbarWidgetWindow.UsageRequested += OnTaskbarUsageRequested;
        _taskbarWidgetWindow.ExitRequested += OnExitRequested;
        _taskbarWidgetWindow.Closed += OnTaskbarWidgetClosed;
        _taskbarWidgetWindow.ShowNoActivate();
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _usageService?.Dispose();
        SettingsWindow.CloseInstance();
        _flyoutWindow?.Close();
        _taskbarWidgetWindow?.Close();
        Exit();
    }

    // The widget lives as a child of Shell_TrayWnd, so an Explorer restart
    // destroys it together with the taskbar. Wait for the new taskbar to come
    // up and settle, then rebuild the widget from scratch.
    private void OnTaskbarWidgetClosed(object sender, WindowEventArgs args)
    {
        if (_taskbarWidgetWindow is not null)
        {
            _taskbarWidgetWindow.UsageRequested -= OnTaskbarUsageRequested;
            _taskbarWidgetWindow.ExitRequested -= OnExitRequested;
            _taskbarWidgetWindow.Closed -= OnTaskbarWidgetClosed;
            _taskbarWidgetWindow = null;
        }

        if (_exiting || _widgetRecoveryTimer is not null)
        {
            return;
        }

        _taskbarSettleTicks = 0;
        _widgetRecoveryTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _widgetRecoveryTimer.Interval = TimeSpan.FromSeconds(1);
        _widgetRecoveryTimer.Tick += OnWidgetRecoveryTick;
        _widgetRecoveryTimer.Start();
    }

    private void OnWidgetRecoveryTick(DispatcherQueueTimer sender, object args)
    {
        if (NativeMethods.FindPrimaryTaskbar() == IntPtr.Zero)
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
        CreateTaskbarWidget();
    }

    private void OnTaskbarUsageRequested(object? sender, EventArgs e)
    {
        if (_usageService is null)
        {
            _usageService = new UsageService();
            _usageService.Start();
        }

        _flyoutWindow ??= new FlyoutWindow(_usageService);
        _flyoutWindow.ToggleNoActivate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine(e.Exception);
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        _usageService?.Dispose();
    }
}
