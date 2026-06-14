using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.Graphics;
using Color = Windows.UI.Color;

namespace FluentAgentBar;

public sealed partial class TaskbarWidgetWindow : Window
{
    private const int WidgetLogicalWidth = 304;
    private const int WidgetLogicalHeight = 40;
    private const int TrayGap = 8;
    private const int WatchdogTickInterval = 50;
    private const int WatchdogPositionTolerance = 6;
    private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(6);

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherQueueTimer _clickTimer;
    private readonly DispatcherQueueTimer _frameFixTimer;
    private readonly DispatcherQueueTimer _cycleTimer;
    private readonly UsageService _usageService;
    private readonly TaskbarTarget _target;
    private bool _mouseWasDown;
    private bool _rightMouseWasDown;
    private bool _darkTaskbar = true;
    private int _watchdogTicks;
    private int _cycleIndex;
    private bool _anchored;
    private bool _targetLostReported;
    private MenuFlyout? _contextMenu;
    private ToggleMenuFlyoutItem? _glowToggleItem;
    private ToggleMenuFlyoutItem? _acrylicToggleItem;
    private ToggleMenuFlyoutItem? _startupToggleItem;

    public event EventHandler? UsageRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? TargetLost;

    internal IntPtr TargetHwnd => _target.Hwnd;

    public Geometry LogoGeometry { get; private set; } = ProviderIcons.GeometryFor("Codex");
    public Brush LogoBrush { get; private set; } = ProviderIcons.BrushFor("Codex");
    public Brush TintBrush { get; private set; } = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    public string ProfileTitle { get; private set; } = string.Empty;
    public string ProfilePlan { get; private set; } = string.Empty;
    public Visibility PlanVisibility { get; private set; } = Visibility.Collapsed;
    public string ProfileLabel { get; private set; } = MockUsageData.PrimaryProfile.Label;
    public string PrimaryQuotaLabel { get; private set; } = "5h";
    public int RemainingPercent { get; private set; } = MockUsageData.PrimaryProfile.RemainingPercent;
    public string RemainingText { get; private set; } = MockUsageData.PrimaryProfile.RemainingText;
    public string WeeklyQuotaLabel { get; private set; } = "Wk";
    public int WeeklyPercent { get; private set; } = MockUsageData.PrimaryProfile.WeeklyPercent;
    public string WeeklyText { get; private set; } = MockUsageData.PrimaryProfile.WeeklyText;
    public GridLength PrimaryFillWidth { get; private set; } = new(72, GridUnitType.Star);
    public GridLength PrimaryRestWidth { get; private set; } = new(28, GridUnitType.Star);
    public GridLength WeeklyFillWidth { get; private set; } = new(54, GridUnitType.Star);
    public GridLength WeeklyRestWidth { get; private set; } = new(46, GridUnitType.Star);
    public Brush TrackBrush { get; private set; } = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
    public Brush TextPrimaryBrush { get; private set; } = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
    public Brush TextSecondaryBrush { get; private set; } = new SolidColorBrush(Color.FromArgb(198, 255, 255, 255));
    public Brush PrimaryFillBrush { get; private set; } = new SolidColorBrush(Color.FromArgb(255, 96, 205, 255));
    public Brush WeeklyFillBrush { get; private set; } = new SolidColorBrush(Color.FromArgb(255, 96, 205, 255));

    internal TaskbarWidgetWindow(UsageService usageService, TaskbarTarget target)
    {
        _usageService = usageService;
        _target = target;
        InitializeComponent();
        RefreshConfigBindings();
        Bindings.Update();

        Title = "Fluent AgentBar Widget";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new TransparentBackdrop();

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigurePresenter();
        ConfigureNativeWindow();

        _clickTimer = DispatcherQueue.CreateTimer();
        _clickTimer.Interval = TimeSpan.FromMilliseconds(40);
        _clickTimer.Tick += OnClickTimerTick;
        _frameFixTimer = DispatcherQueue.CreateTimer();
        _frameFixTimer.Interval = TimeSpan.FromMilliseconds(200);
        _frameFixTimer.IsRepeating = false;
        _frameFixTimer.Tick += OnFrameFixTick;
        _cycleTimer = DispatcherQueue.CreateTimer();
        _cycleTimer.Interval = CycleInterval;
        _cycleTimer.Tick += OnCycleTimerTick;
        _cycleTimer.Start();
        AppConfigStore.Changed += OnConfigChanged;
        _usageService.Updated += OnUsageUpdated;
        Closed += OnClosed;
    }

    public void ShowNoActivate()
    {
        RefreshConfigBindings();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        PositionInTaskbar();
        _clickTimer.Start();
        _frameFixTimer.Start();
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshConfigBindings);
    }

    private void OnUsageUpdated(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshConfigBindings);
    }

    private void OnCycleTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (BuildEntries(AppConfigStore.Load()).Count > 1)
        {
            _cycleIndex++;
            RefreshConfigBindings();
        }
    }

    // The widget rotates through every enabled profile of every provider;
    // the brand mark, profile name and a faint brand-colored glow identify
    // which account is currently displayed.
    private IReadOnlyList<(string Provider, ProfileUsage Profile)> BuildEntries(AppConfig config)
    {
        List<(string, ProfileUsage)> entries = [];
        foreach (ProviderUsage provider in _usageService.Providers)
        {
            foreach (ProfileUsage profile in provider.Profiles)
            {
                entries.Add((provider.Name, profile));
            }
        }

        if (entries.Count == 0)
        {
            ProfileConfig configuredProfile = config.Profiles.FirstOrDefault(profile => profile.Enabled)
                ?? config.Profiles.First();
            string providerName = AppConfigStore.IsProvider(configuredProfile, "claude") ? "Claude" : "Codex";
            entries.Add((providerName, MockUsageData.CreateUnavailableProfile(configuredProfile)));
        }

        return entries;
    }

    private void RefreshConfigBindings()
    {
        AppConfig config = AppConfigStore.Load();
        IReadOnlyList<(string Provider, ProfileUsage Profile)> entries = BuildEntries(config);
        (string providerName, ProfileUsage profile) = entries[((_cycleIndex % entries.Count) + entries.Count) % entries.Count];

        LogoGeometry = ProviderIcons.GeometryFor(providerName);
        LogoBrush = ProviderIcons.BrushFor(providerName);
        ProfileTitle = profile.Label;
        ProfilePlan = profile.PlanDisplay;
        PlanVisibility = ProfilePlan.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfileLabel = $"{providerName} · {profile.Label}";
        PrimaryQuotaLabel = "5h";
        RemainingPercent = profile.RemainingPercent;
        RemainingText = profile.RemainingText;
        WeeklyQuotaLabel = "Wk";
        WeeklyPercent = profile.WeeklyPercent;
        WeeklyText = profile.WeeklyText;
        PrimaryFillWidth = new GridLength(Math.Clamp(RemainingPercent, 0, 100), GridUnitType.Star);
        PrimaryRestWidth = new GridLength(100 - Math.Clamp(RemainingPercent, 0, 100), GridUnitType.Star);
        WeeklyFillWidth = new GridLength(Math.Clamp(WeeklyPercent, 0, 100), GridUnitType.Star);
        WeeklyRestWidth = new GridLength(100 - Math.Clamp(WeeklyPercent, 0, 100), GridUnitType.Star);
        _darkTaskbar = TaskbarTheme.IsDark();
        // Popups opened from the widget (tooltip, context menu) must follow
        // the taskbar theme, not the apps theme — same split as the flyout.
        Shell.RequestedTheme = _darkTaskbar ? ElementTheme.Dark : ElementTheme.Light;
        TrackBrush = new SolidColorBrush(_darkTaskbar
            ? Color.FromArgb(48, 255, 255, 255)
            : Color.FromArgb(40, 0, 0, 0));
        TextPrimaryBrush = new SolidColorBrush(_darkTaskbar
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(228, 0, 0, 0));
        TextSecondaryBrush = new SolidColorBrush(_darkTaskbar
            ? Color.FromArgb(198, 255, 255, 255)
            : Color.FromArgb(158, 0, 0, 0));
        TintBrush = config.WidgetGlowEnabled
            ? CreateTintBrush(providerName)
            : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        ApplyStatusBrushes();
        Bindings.Update();
    }

    // A very light brand-colored glow fading out to the right, so the
    // provider reads at a glance without breaking the taskbar blend.
    private Brush CreateTintBrush(string providerName)
    {
        bool isClaude = providerName.Contains("claude", StringComparison.OrdinalIgnoreCase);
        Color brand = isClaude
            ? Color.FromArgb(255, 217, 119, 87)
            : (_darkTaskbar ? Color.FromArgb(255, 96, 205, 255) : Color.FromArgb(255, 0, 103, 192));
        byte alpha = (byte)(_darkTaskbar ? 34 : 26);

        LinearGradientBrush brush = new()
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0)
        };
        brush.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb(alpha, brand.R, brand.G, brand.B),
            Offset = 0
        });
        brush.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb((byte)(alpha / 2), brand.R, brand.G, brand.B),
            Offset = 0.45
        });
        brush.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb(0, brand.R, brand.G, brand.B),
            Offset = 1
        });
        return brush;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _clickTimer.Stop();
        _frameFixTimer.Stop();
        _cycleTimer.Stop();
        AppConfigStore.Changed -= OnConfigChanged;
        _usageService.Updated -= OnUsageUpdated;
    }

    private void ConfigurePresenter()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
        }

        SizeInt32 physicalSize = GetPhysicalWidgetSize();
        _appWindow.Resize(physicalSize);
    }

    private void ConfigureNativeWindow()
    {
        NativeMethods.AddExtendedStyle(
            _hwnd,
            NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW
        );
        NativeMethods.RemoveExtendedStyle(_hwnd, NativeMethods.WS_EX_APPWINDOW);
        NativeMethods.EnableTransparentComposition(_hwnd);
        NativeMethods.RemoveWindowBorder(_hwnd);
    }

    private void PositionInTaskbar()
    {
        IntPtr taskbar = _target.Hwnd;
        if (!IsTargetCurrent() || !NativeMethods.GetWindowRect(taskbar, out NativeMethods.Rect taskbarRect))
        {
            ReportTargetLost();
            return;
        }

        SizeInt32 physicalSize = GetPhysicalWidgetSize(taskbar);
        NativeMethods.Rect trayRect = NativeMethods.TryGetTrayRect(taskbar, taskbarRect);

        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        bool horizontal = taskbarWidth >= taskbarHeight;

        int x;
        int y;

        if (horizontal)
        {
            int trayLeft = trayRect.Left > 0 ? trayRect.Left : taskbarRect.Right;
            int relativeX = Math.Max(TrayGap, trayLeft - taskbarRect.Left - physicalSize.Width - TrayGap);
            int relativeY = Math.Max(0, (taskbarHeight - physicalSize.Height) / 2);
            x = taskbarRect.Left + relativeX;
            y = taskbarRect.Top + relativeY;
        }
        else
        {
            int trayTop = trayRect.Top > 0 ? trayRect.Top : taskbarRect.Bottom;
            int relativeX = Math.Max(0, (taskbarWidth - physicalSize.Width) / 2);
            int relativeY = Math.Max(TrayGap, trayTop - taskbarRect.Top - physicalSize.Height - TrayGap);
            x = taskbarRect.Left + relativeX;
            y = taskbarRect.Top + relativeY;
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            physicalSize.Width,
            physicalSize.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW
        );

        double scale = physicalSize.Height / (double)WidgetLogicalHeight;
        int radius = Math.Max(8, (int)Math.Round(8 * scale));
        NativeMethods.SetRoundedRegion(_hwnd, physicalSize.Width, physicalSize.Height, radius);
    }

    // Reparenting or stripping the caption frame before the XAML island has
    // presented its first frame stops it from rendering at all, so both are
    // deferred until shortly after the initial show.
    private void OnFrameFixTick(DispatcherQueueTimer sender, object args)
    {
        NativeMethods.RemoveStyle(_hwnd, NativeMethods.WS_CAPTION);
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_FRAMECHANGED
        );
        AdoptTaskbar();
    }

    // FluentFlyout reparents its widget into Shell_TrayWnd, but a
    // WinUI 3 island stops compositing entirely as a cross-process child —
    // that path only works for WPF. Owning is the next best invariant: the
    // window manager keeps an owned window above its owner, so whenever
    // Explorer raises the taskbar, the widget is raised with it and can never
    // end up covered by it. The window stays top-level, so composition and
    // per-pixel transparency keep working.
    private void AdoptTaskbar()
    {
        IntPtr taskbar = _target.Hwnd;
        if (!IsTargetCurrent())
        {
            ReportTargetLost();
            return;
        }

        NativeMethods.SetWindowOwner(_hwnd, taskbar);
        _anchored = true;
        PositionInTaskbar();

        // Owner changes on a visible window only take effect reliably after a
        // visibility bounce.
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
    }

    private void ApplyStatusBrushes()
    {
        PrimaryFillBrush = new SolidColorBrush(StatusFillColor(RemainingPercent));
        WeeklyFillBrush = new SolidColorBrush(StatusFillColor(WeeklyPercent));
    }

    private Color StatusFillColor(int remainingPercent)
    {
        if (remainingPercent <= 15)
        {
            return _darkTaskbar
                ? Color.FromArgb(255, 255, 153, 164)
                : Color.FromArgb(255, 196, 43, 28);
        }

        if (remainingPercent <= 30)
        {
            return _darkTaskbar
                ? Color.FromArgb(255, 252, 225, 0)
                : Color.FromArgb(255, 157, 93, 0);
        }

        return _darkTaskbar
            ? Color.FromArgb(255, 96, 205, 255)
            : Color.FromArgb(255, 0, 103, 192);
    }

    private SizeInt32 GetPhysicalWidgetSize()
    {
        return GetPhysicalWidgetSize(_target.Hwnd);
    }

    private SizeInt32 GetPhysicalWidgetSize(IntPtr taskbar)
    {
        uint dpi = taskbar != IntPtr.Zero
            ? NativeMethods.GetDpiForWindow(taskbar)
            : NativeMethods.GetDpiForWindow(_hwnd);
        double scale = Math.Max(1.0, dpi / 96.0);
        return new SizeInt32(
            (int)Math.Round(WidgetLogicalWidth * scale),
            (int)Math.Round(WidgetLogicalHeight * scale)
        );
    }

    private void OnClickTimerTick(DispatcherQueueTimer sender, object args)
    {
        _watchdogTicks++;
        if (_watchdogTicks >= WatchdogTickInterval)
        {
            _watchdogTicks = 0;
            VerifyWidgetStability();
        }

        bool leftDown = NativeMethods.IsLeftMouseButtonDown();
        bool rightDown = NativeMethods.IsRightMouseButtonDown();
        bool leftClickedNow = leftDown && !_mouseWasDown;
        bool rightClickedNow = rightDown && !_rightMouseWasDown;
        _mouseWasDown = leftDown;
        _rightMouseWasDown = rightDown;

        if ((!leftClickedNow && !rightClickedNow) ||
            !NativeMethods.GetCursorPos(out NativeMethods.Point cursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out NativeMethods.Rect rect))
        {
            return;
        }

        bool inside =
            cursor.X >= rect.Left &&
            cursor.X <= rect.Right &&
            cursor.Y >= rect.Top &&
            cursor.Y <= rect.Bottom;

        if (!inside)
        {
            return;
        }

        if (leftClickedNow)
        {
            UsageRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ShowContextMenu(cursor, rect);
        }
    }

    private void ShowContextMenu(NativeMethods.Point cursor, NativeMethods.Rect rect)
    {
        _contextMenu ??= CreateContextMenu();

        AppConfig config = AppConfigStore.Load();
        _glowToggleItem!.IsChecked = config.WidgetGlowEnabled;
        _acrylicToggleItem!.IsChecked = string.Equals(config.FlyoutStyle, "acrylic", StringComparison.OrdinalIgnoreCase);
        _startupToggleItem!.IsChecked = WindowsStartupService.IsEnabled();

        double scale = Math.Max(1.0, Shell.XamlRoot?.RasterizationScale ?? 1.0);
        _contextMenu.ShowAt(Shell, new FlyoutShowOptions
        {
            Position = new Windows.Foundation.Point(
                (cursor.X - rect.Left) / scale,
                (cursor.Y - rect.Top) / scale),
            Placement = FlyoutPlacementMode.Top
        });
    }

    private MenuFlyout CreateContextMenu()
    {
        MenuFlyout menu = new();

        MenuFlyoutItem refreshItem = new() { Text = "Refresh now", Icon = new FontIcon { Glyph = "" } };
        refreshItem.Click += (_, _) => _ = _usageService.FetchAsync();

        MenuFlyoutItem settingsItem = new() { Text = "Settings", Icon = new FontIcon { Glyph = "" } };
        settingsItem.Click += (_, _) => SettingsWindow.ShowInstance();

        MenuFlyoutItem configItem = new() { Text = "Open config file", Icon = new FontIcon { Glyph = "" } };
        configItem.Click += OnOpenConfigClick;

        _glowToggleItem = new ToggleMenuFlyoutItem { Text = "Widget glow" };
        _glowToggleItem.Click += (_, _) =>
        {
            AppConfig config = AppConfigStore.Load();
            config.WidgetGlowEnabled = !config.WidgetGlowEnabled;
            AppConfigStore.Save(config);
        };

        _startupToggleItem = new ToggleMenuFlyoutItem { Text = "Start with Windows" };
        _startupToggleItem.Click += (_, _) =>
        {
            bool requested = _startupToggleItem.IsChecked;
            if (!WindowsStartupService.TrySetEnabled(requested, out string errorMessage))
            {
                _startupToggleItem.IsChecked = WindowsStartupService.IsEnabled();
                System.Diagnostics.Debug.WriteLine(errorMessage);
            }
        };

        _acrylicToggleItem = new ToggleMenuFlyoutItem { Text = "Acrylic flyout" };
        _acrylicToggleItem.Click += (_, _) =>
        {
            AppConfig config = AppConfigStore.Load();
            config.FlyoutStyle = string.Equals(config.FlyoutStyle, "acrylic", StringComparison.OrdinalIgnoreCase)
                ? "solid"
                : "acrylic";
            AppConfigStore.Save(config);
        };

        MenuFlyoutItem exitItem = new() { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(refreshItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(configItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_startupToggleItem);
        menu.Items.Add(_glowToggleItem);
        menu.Items.Add(_acrylicToggleItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private void OnOpenConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppConfigStore.EnsureConfigFileExists();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppConfigStore.ConfigPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // Once owned by the taskbar, the window manager guarantees the widget
    // stays above it; the watchdog only retries a failed anchor and follows
    // geometry changes (DPI, taskbar resize). An Explorer restart destroys
    // this window with its owner, and App recreates it from scratch.
    private void VerifyWidgetStability()
    {
        if (!IsTargetCurrent())
        {
            ReportTargetLost();
            return;
        }

        if (NativeMethods.IsForegroundWindowFullscreenOnMonitor(_hwnd))
        {
            return;
        }

        IntPtr taskbar = _target.Hwnd;

        if (!_anchored)
        {
            AdoptTaskbar();
            return;
        }

        if (!NativeMethods.IsWindowVisible(_hwnd) || !IsNearExpectedTaskbarPosition(taskbar))
        {
            PositionInTaskbar();
        }
    }

    private bool IsTargetCurrent()
    {
        return _target.Hwnd != IntPtr.Zero &&
            NativeMethods.FindTaskbars().Any(target => target.Hwnd == _target.Hwnd);
    }

    private void ReportTargetLost()
    {
        if (_targetLostReported)
        {
            return;
        }

        _targetLostReported = true;
        TargetLost?.Invoke(this, EventArgs.Empty);
    }

    private bool IsNearExpectedTaskbarPosition(IntPtr taskbar)
    {
        if (!NativeMethods.GetWindowRect(taskbar, out NativeMethods.Rect taskbarRect) ||
            !NativeMethods.GetWindowRect(_hwnd, out NativeMethods.Rect widgetRect))
        {
            return false;
        }

        SizeInt32 physicalSize = GetPhysicalWidgetSize(taskbar);
        NativeMethods.Rect trayRect = NativeMethods.TryGetTrayRect(taskbar, taskbarRect);

        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        bool horizontal = taskbarWidth >= taskbarHeight;

        int expectedX;
        int expectedY;

        if (horizontal)
        {
            int trayLeft = trayRect.Left > 0 ? trayRect.Left : taskbarRect.Right;
            int relativeX = Math.Max(TrayGap, trayLeft - taskbarRect.Left - physicalSize.Width - TrayGap);
            int relativeY = Math.Max(0, (taskbarHeight - physicalSize.Height) / 2);
            expectedX = taskbarRect.Left + relativeX;
            expectedY = taskbarRect.Top + relativeY;
        }
        else
        {
            int trayTop = trayRect.Top > 0 ? trayRect.Top : taskbarRect.Bottom;
            int relativeX = Math.Max(0, (taskbarWidth - physicalSize.Width) / 2);
            int relativeY = Math.Max(TrayGap, trayTop - taskbarRect.Top - physicalSize.Height - TrayGap);
            expectedX = taskbarRect.Left + relativeX;
            expectedY = taskbarRect.Top + relativeY;
        }

        int actualWidth = widgetRect.Right - widgetRect.Left;
        int actualHeight = widgetRect.Bottom - widgetRect.Top;

        return
            Math.Abs(widgetRect.Left - expectedX) <= WatchdogPositionTolerance &&
            Math.Abs(widgetRect.Top - expectedY) <= WatchdogPositionTolerance &&
            Math.Abs(actualWidth - physicalSize.Width) <= WatchdogPositionTolerance &&
            Math.Abs(actualHeight - physicalSize.Height) <= WatchdogPositionTolerance;
    }
}
