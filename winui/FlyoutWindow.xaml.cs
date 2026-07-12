using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;
using Windows.Graphics;

namespace FluentAgentBar;

public sealed partial class FlyoutWindow : Window
{
    private const int FlyoutLogicalWidth = 400;
    private const int MinFlyoutLogicalHeight = 220;
    private const int EdgeGap = 8;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherQueueTimer _outsideClickTimer;
    private readonly DispatcherQueueTimer _frameFixTimer;
    private readonly UsageService _usageService;
    private DateTimeOffset _shownAt;
    private bool _isClosing;
    private bool _mouseWasDown;
    private string _backdropStyle = "acrylic";
    private double _logicalHeight = 462;
    private readonly HashSet<string> _expandedHistories = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ProviderUsage>? _providersSource;
    private const double ResizeAnimationMs = 160;
    private DispatcherQueueTimer? _resizeTimer;
    private RectInt32 _resizeFrom;
    private RectInt32 _resizeTarget;
    private DateTimeOffset _resizeAnimationStart;
    private bool _isSwitchingCodexProfile;
    private bool _heightUpdatePending;
    private bool _pendingHeightAnimation;

    public IReadOnlyList<ProviderUsage> Providers { get; private set; } = MockUsageData.CreateProviders();
    public string LastRefreshText { get; private set; } = "Last refresh --";
    public string IntervalText { get; private set; } = "Refresh interval 5 min";
    public string CodexSwitchStatusText { get; private set; } = string.Empty;
    public Visibility CodexSwitchStatusVisibility =>
        string.IsNullOrWhiteSpace(CodexSwitchStatusText) ? Visibility.Collapsed : Visibility.Visible;

    public FlyoutWindow(UsageService usageService)
    {
        _usageService = usageService;
        if (Environment.GetCommandLineArgs().Any(arg => arg.Equals("--expand-history", StringComparison.OrdinalIgnoreCase)))
        {
            _expandedHistories.Add("Codex");
            _expandedHistories.Add("Claude");
        }

        InitializeComponent();
        RefreshConfigBindings();
        Bindings.Update();

        Title = "Fluent AgentBar";
        ExtendsContentIntoTitleBar = true;

        // The flyout must match the taskbar theme, not the app/apps theme:
        // the shell taskbar follows SystemUsesLightTheme while regular apps
        // follow AppsUseLightTheme, and the two regularly disagree.
        Shell.RequestedTheme = TaskbarTheme.IsDark() ? ElementTheme.Dark : ElementTheme.Light;
        SystemBackdrop = new ThinAcrylicBackdrop();

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigurePresenter();
        ConfigureNativeWindow();

        Shell.KeyDown += OnShellKeyDown;
        Shell.Loaded += (_, _) => QueueFlyoutHeightUpdate();
        _outsideClickTimer = DispatcherQueue.CreateTimer();
        _outsideClickTimer.Interval = TimeSpan.FromMilliseconds(50);
        _outsideClickTimer.Tick += OnOutsideClickTimerTick;
        _frameFixTimer = DispatcherQueue.CreateTimer();
        _frameFixTimer.Interval = TimeSpan.FromMilliseconds(200);
        _frameFixTimer.IsRepeating = false;
        _frameFixTimer.Tick += OnFrameFixTick;

        if (Shell.Resources["CloseStoryboard"] is Storyboard closeStoryboard)
        {
            closeStoryboard.Completed += OnCloseStoryboardCompleted;
        }
        AppConfigStore.Changed += OnConfigChanged;
        _usageService.Updated += OnUsageUpdated;
        Closed += OnClosed;
    }

    public void ShowNoActivate()
    {
        RefreshConfigBindings();
        SetCodexSwitchStatus(string.Empty, animate: false);
        _isClosing = false;
        _mouseWasDown = true;
        _shownAt = DateTimeOffset.Now;
        Shell.Opacity = 0;
        Shell.IsHitTestVisible = true;
        ShellTransform.TranslateY = 8;

        PositionAboveTaskbar();
        _appWindow.Show(false);
        NativeMethods.SetTopMostNoActivate(_hwnd);

        if (Shell.Resources["OpenStoryboard"] is Storyboard storyboard)
        {
            storyboard.Begin();
        }

        _outsideClickTimer.Start();
        _frameFixTimer.Start();
    }

    private void OnFrameFixTick(DispatcherQueueTimer sender, object args)
    {
        // Same caveat as the taskbar widget: the caption frame draws a bright
        // outline, but stripping it before the first presented frame breaks
        // XAML island rendering, so it is deferred until after the show.
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
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshConfigBindings);
    }

    private void OnUsageUpdated(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshConfigBindings);
    }

    private void RefreshConfigBindings()
    {
        AppConfig config = AppConfigStore.Load();

        // Re-templating the provider cards replays every ProgressBar fill
        // animation, so only swap the list when the service actually
        // published new data — not on every flyout open or config touch.
        IReadOnlyList<ProviderUsage> source = _usageService.Providers;
        if (!ReferenceEquals(source, _providersSource))
        {
            _providersSource = source;
            foreach (ProviderUsage provider in source)
            {
                provider.IsHistoryExpanded = _expandedHistories.Contains(provider.Name);
            }

            Providers = source;
        }
        LastRefreshText = _usageService.LastRefresh is { } lastRefresh
            ? $"Last refresh {lastRefresh.LocalDateTime:t}"
            : "Last refresh --";
        IntervalText = $"Refresh interval {Math.Max(1, config.RefreshIntervalSeconds / 60)} min";
        ApplyBackdrop(config.FlyoutStyle);
        Bindings.Update();
        QueueFlyoutHeightUpdate();
    }

    private void QueueFlyoutHeightUpdate(bool animate = false)
    {
        _pendingHeightAnimation |= animate;
        if (_heightUpdatePending)
        {
            return;
        }

        _heightUpdatePending = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _heightUpdatePending = false;
            bool shouldAnimate = _pendingHeightAnimation;
            _pendingHeightAnimation = false;
            UpdateFlyoutHeight(shouldAnimate);
        });
    }

    // The flyout has no inner scrollbar: it grows with its content, capped to
    // the space available above the taskbar.
    private void UpdateFlyoutHeight(bool animate = false)
    {
        // Measuring before the XAML island has loaded crashes WinUI with a
        // stowed exception (0xc000027b), so wait for Shell.Loaded.
        if (!Shell.IsLoaded)
        {
            return;
        }

        double desired;
        try
        {
            Shell.Measure(new Windows.Foundation.Size(FlyoutLogicalWidth, double.PositiveInfinity));
            desired = Math.Ceiling(Shell.DesiredSize.Height);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return;
        }
        if (desired <= 0)
        {
            return;
        }

        double previousHeight = _logicalHeight;
        _logicalHeight = Math.Max(desired, MinFlyoutLogicalHeight);

        if (Math.Abs(_logicalHeight - previousHeight) > 0.5 &&
            NativeMethods.IsWindowVisible(_hwnd) &&
            !_isClosing)
        {
            PositionAboveTaskbar(animate);
        }
    }

    private void ApplyBackdrop(string style)
    {
        if (string.Equals(_backdropStyle, style, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _backdropStyle = style;
        if (string.Equals(style, "solid", StringComparison.OrdinalIgnoreCase))
        {
            SystemBackdrop = null;
            Shell.Background = new SolidColorBrush(Shell.RequestedTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        }
        else
        {
            SystemBackdrop = new ThinAcrylicBackdrop();
            Shell.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }
    }

    public void PrepareHidden()
    {
        PositionAboveTaskbar();
        _appWindow.Show(false);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    }

    public void ToggleNoActivate()
    {
        if (NativeMethods.IsWindowVisible(_hwnd) && !_isClosing)
        {
            BeginHide();
            return;
        }

        ShowNoActivate();
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

        SizeInt32 physicalSize = GetPhysicalFlyoutSize();
        _appWindow.Resize(physicalSize);
    }

    private void ConfigureNativeWindow()
    {
        NativeMethods.AddExtendedStyle(
            _hwnd,
            NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW
        );
        NativeMethods.RemoveExtendedStyle(_hwnd, NativeMethods.WS_EX_APPWINDOW);
        NativeMethods.SetRoundedCorners(_hwnd);
        if (Shell.RequestedTheme == ElementTheme.Dark)
        {
            NativeMethods.SetWindowBorderColor(_hwnd, 70, 70, 76);
        }
    }

    private void PositionAboveTaskbar(bool animate = false)
    {
        NativeMethods.Rect taskbarRect = NativeMethods.GetPrimaryTaskbarRect();
        NativeMethods.Rect trayRect = NativeMethods.TryGetTrayRect(taskbarRect);
        NativeMethods.Rect monitorRect = NativeMethods.GetMonitorRectForWindow(
            NativeMethods.FindPrimaryTaskbar()
        );
        SizeInt32 physicalSize = GetPhysicalFlyoutSize();

        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        bool horizontal = taskbarWidth >= taskbarHeight;
        bool bottom = horizontal && taskbarRect.Top > monitorRect.Top + ((monitorRect.Bottom - monitorRect.Top) / 2);
        bool top = horizontal && !bottom;
        bool right = !horizontal && taskbarRect.Left > monitorRect.Left + ((monitorRect.Right - monitorRect.Left) / 2);

        int x;
        int y;

        if (horizontal)
        {
            int anchorLeft = trayRect.Left > 0 ? trayRect.Left : taskbarRect.Right - EdgeGap;
            x = Math.Clamp(anchorLeft - physicalSize.Width - EdgeGap, monitorRect.Left + EdgeGap, monitorRect.Right - physicalSize.Width - EdgeGap);
            y = bottom
                ? taskbarRect.Top - physicalSize.Height - EdgeGap
                : taskbarRect.Bottom + EdgeGap;
        }
        else
        {
            int anchorTop = trayRect.Top > 0 ? trayRect.Top : taskbarRect.Bottom - EdgeGap;
            x = right
                ? taskbarRect.Left - physicalSize.Width - EdgeGap
                : taskbarRect.Right + EdgeGap;
            y = Math.Clamp(anchorTop - physicalSize.Height - EdgeGap, monitorRect.Top + EdgeGap, monitorRect.Bottom - physicalSize.Height - EdgeGap);
        }

        if (top)
        {
            y = Math.Min(y, monitorRect.Bottom - physicalSize.Height - EdgeGap);
        }

        RectInt32 target = new(x, y, physicalSize.Width, physicalSize.Height);
        if (animate && NativeMethods.IsWindowVisible(_hwnd))
        {
            StartResizeAnimation(target);
        }
        else
        {
            StopResizeAnimation();
            _appWindow.MoveAndResize(target);
        }
    }

    // The flyout is anchored above the taskbar, so growing means moving the
    // top edge up; doing that in one jump clips the content for a frame and
    // reads as flicker. A short eased glide (synced with the panel fade-in)
    // hides it. Re-toggling mid-flight just retargets the animation.
    private void StartResizeAnimation(RectInt32 target)
    {
        _resizeFrom = new RectInt32(
            _appWindow.Position.X,
            _appWindow.Position.Y,
            _appWindow.Size.Width,
            _appWindow.Size.Height);
        _resizeTarget = target;
        _resizeAnimationStart = DateTimeOffset.Now;

        if (_resizeTimer is null)
        {
            _resizeTimer = DispatcherQueue.CreateTimer();
            _resizeTimer.Interval = TimeSpan.FromMilliseconds(15);
            _resizeTimer.Tick += OnResizeAnimationTick;
        }

        _resizeTimer.Start();
    }

    private void StopResizeAnimation()
    {
        _resizeTimer?.Stop();
    }

    private void OnResizeAnimationTick(DispatcherQueueTimer sender, object args)
    {
        double progress = Math.Clamp(
            (DateTimeOffset.Now - _resizeAnimationStart).TotalMilliseconds / ResizeAnimationMs,
            0,
            1);
        double eased = 1 - Math.Pow(1 - progress, 3);

        _appWindow.MoveAndResize(new RectInt32(
            Lerp(_resizeFrom.X, _resizeTarget.X, eased),
            Lerp(_resizeFrom.Y, _resizeTarget.Y, eased),
            Lerp(_resizeFrom.Width, _resizeTarget.Width, eased),
            Lerp(_resizeFrom.Height, _resizeTarget.Height, eased)));

        if (progress >= 1)
        {
            sender.Stop();
        }
    }

    private static int Lerp(int from, int to, double eased)
    {
        return (int)Math.Round(from + ((to - from) * eased));
    }

    private SizeInt32 GetPhysicalFlyoutSize()
    {
        IntPtr taskbar = NativeMethods.FindPrimaryTaskbar();
        uint dpi = taskbar != IntPtr.Zero
            ? NativeMethods.GetDpiForWindow(taskbar)
            : NativeMethods.GetDpiForWindow(_hwnd);
        double scale = Math.Max(1.0, dpi / 96.0);
        int physicalHeight = (int)Math.Round(_logicalHeight * scale);

        // Never taller than the work area above/beside the taskbar.
        NativeMethods.Rect taskbarRect = NativeMethods.GetPrimaryTaskbarRect();
        NativeMethods.Rect monitorRect = NativeMethods.GetMonitorRectForWindow(taskbar);
        int taskbarHeight = Math.Max(0, taskbarRect.Bottom - taskbarRect.Top);
        int maxHeight = (monitorRect.Bottom - monitorRect.Top) - taskbarHeight - (EdgeGap * 2);
        if (maxHeight > 0)
        {
            physicalHeight = Math.Min(physicalHeight, maxHeight);
        }

        return new SizeInt32(
            (int)Math.Round(FlyoutLogicalWidth * scale),
            physicalHeight
        );
    }

    // PointerPressed instead of Tapped: rapid clicks make the gesture
    // recognizer classify every second click as a double-tap, which never
    // raises Tapped — presses always count. The provider travels through
    // Tag="{x:Bind}" because ItemsRepeater never sets DataContext, and
    // expansion is toggled in place (INotifyPropertyChanged + OneWay
    // bindings) so nothing is re-templated.
    private void OnProviderHeaderPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProviderUsage provider } ||
            !provider.HasHistory)
        {
            return;
        }

        provider.IsHistoryExpanded = !provider.IsHistoryExpanded;
        if (provider.IsHistoryExpanded)
        {
            _expandedHistories.Add(provider.Name);
        }
        else
        {
            _expandedHistories.Remove(provider.Name);
        }

        QueueFlyoutHeightUpdate(animate: true);
    }

    // A soft fade + upward slide whenever the history panel appears; the
    // implicit animation also covers re-opens of the flyout, and spamming the
    // toggle just restarts the show animation instead of re-building visuals.
    private void OnHistoryPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        Microsoft.UI.Composition.Compositor compositor =
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element).Compositor;
        Microsoft.UI.Composition.CompositionEasingFunction ease =
            compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1f));

        Microsoft.UI.Composition.ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(220);

        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        Microsoft.UI.Composition.Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0f, new System.Numerics.Vector3(0f, -8f, 0f));
        slide.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(220);

        Microsoft.UI.Composition.CompositionAnimationGroup group = compositor.CreateAnimationGroup();
        group.Add(fade);
        group.Add(slide);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetImplicitShowAnimation(element, group);
    }

    private void OnShellKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            BeginHide();
            e.Handled = true;
        }
    }

    private void OnOutsideClickTimerTick(DispatcherQueueTimer sender, object args)
    {
        bool mouseDown = NativeMethods.IsAnyMouseButtonDown();
        bool clickedNow = mouseDown && !_mouseWasDown;
        _mouseWasDown = mouseDown;

        if (!clickedNow || DateTimeOffset.Now - _shownAt < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out NativeMethods.Point cursor) ||
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
            BeginHide();
        }
    }

    public void BeginHide()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        Shell.IsHitTestVisible = false;
        _outsideClickTimer.Stop();
        StopResizeAnimation();

        if (Shell.Resources["CloseStoryboard"] is Storyboard storyboard)
        {
            storyboard.Begin();
        }
        else
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        }
    }

    private void OnCloseStoryboardCompleted(object? sender, object e)
    {
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        _isClosing = false;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        AppConfigStore.Changed -= OnConfigChanged;
        _usageService.Updated -= OnUsageUpdated;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await _usageService.FetchAsync();
    }

    private void OnConfigClick(object sender, RoutedEventArgs e)
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

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsWindow.ShowInstance();
    }

    private async void OnCodexSwitchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProfileUsage profile } ||
            !profile.CanSwitchCodexAccount ||
            _isSwitchingCodexProfile)
        {
            return;
        }

        await SwitchCodexProfileAsync(profile);
    }

    private async Task SwitchCodexProfileAsync(ProfileUsage profile)
    {
        _isSwitchingCodexProfile = true;
        SetCodexSwitchStatus($"Switching Codex to \"{profile.Label}\"...", animate: true);

        try
        {
            ProfileConfig profileConfig = new()
            {
                Provider = "codex",
                Label = profile.Label,
                Home = profile.Home,
                Enabled = true
            };

            CodexAccountSwitchResult result = await CodexAccountSwitchService.SwitchToProfileAsync(profileConfig);
            string processText = result.TargetedProcessCount > 0
                ? $" Closed {result.ClosedProcessCount} Codex session{(result.ClosedProcessCount == 1 ? string.Empty : "s")}."
                : string.Empty;
            string openText = result.CodexOpened
                ? " Codex reopened."
                : $" Codex account switched, but reopen failed: {result.OpenError}";
            SetCodexSwitchStatus($"Switched to \"{result.ProfileLabel}\".{processText}{openText}", animate: true);
            await _usageService.FetchAsync();
        }
        catch (Exception ex)
        {
            SetCodexSwitchStatus($"Codex switch failed: {ex.Message}", animate: true);
        }
        finally
        {
            _isSwitchingCodexProfile = false;
        }
    }

    private void SetCodexSwitchStatus(string text, bool animate)
    {
        CodexSwitchStatusText = text;
        Bindings.Update();
        QueueFlyoutHeightUpdate(animate);
    }
}
