using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WpfPath = System.Windows.Shapes.Path;

namespace FluentAgentBar.WpfTaskbarWidget;

internal sealed class TaskbarWidgetWindow
{
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons");

    private readonly IntPtr _taskbarHwnd;
    private readonly bool _isPrimary;
    private readonly Action _usageRequested;
    private readonly Action<string> _commandInvoked;
    private readonly Action _targetLost;
    private readonly Action _closedUnexpectedly;
    private readonly Action<string>? _log;
    private readonly Window _window;
    private readonly Canvas _canvas;
    private readonly Border _pill;
    private readonly WpfPath _logo;
    private readonly TextBlock _profileTitle;
    private readonly TextBlock _profilePlan;
    private readonly TextBlock _primaryLabel;
    private readonly TextBlock _primaryText;
    private readonly TextBlock _weeklyLabel;
    private readonly TextBlock _weeklyText;
    private readonly Border _primaryTrack;
    private readonly Border _primaryFill;
    private readonly ColumnDefinition _primaryFillColumn;
    private readonly ColumnDefinition _primaryRestColumn;
    private readonly Border _weeklyTrack;
    private readonly Border _weeklyFill;
    private readonly ColumnDefinition _weeklyFillColumn;
    private readonly ColumnDefinition _weeklyRestColumn;
    private readonly Grid _weeklyRow;
    private readonly RowDefinition _weeklySpacer;
    private readonly DispatcherTimer _watchdog;
    private IReadOnlyList<WidgetMenuEntry> _menuEntries;
    private WidgetVisualState _state;
    private HwndSource? _source;
    private HwndSource? _messageSource;
    private uint _taskbarCreatedMessage;
    private IntPtr _hwnd;
    private IntPtr _trayNotifyHwnd;
    private long _originalStyle;
    private long _originalExStyle;
    private bool _closing;
    private bool _targetLostReported;
    private TaskbarWidgetMode _mode;
    private string? _childModeFailure;

    internal TaskbarWidgetWindow(
        IntPtr taskbarHwnd,
        bool isPrimary,
        WidgetVisualState initialState,
        IReadOnlyList<WidgetMenuEntry> initialMenu,
        Action usageRequested,
        Action<string> commandInvoked,
        Action targetLost,
        Action closedUnexpectedly,
        Action<string>? log)
    {
        _taskbarHwnd = taskbarHwnd;
        _isPrimary = isPrimary;
        _state = initialState;
        _menuEntries = initialMenu;
        _usageRequested = usageRequested;
        _commandInvoked = commandInvoked;
        _targetLost = targetLost;
        _closedUnexpectedly = closedUnexpectedly;
        _log = log;

        _canvas = new Canvas { Background = Brushes.Transparent };
        _pill = new Border
        {
            Width = TaskbarWidgetLayoutCalculator.WidgetLogicalWidth,
            Height = TaskbarWidgetLayoutCalculator.WidgetLogicalHeight,
            CornerRadius = new CornerRadius(TaskbarWidgetLayoutCalculator.CornerRadiusLogical),
            Background = Brushes.Transparent
        };

        Grid shell = new() { Margin = new Thickness(10, 5, 10, 5) };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _logo = new WpfPath { Stretch = Stretch.Uniform };
        Viewbox logoBox = new()
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _logo
        };
        Grid.SetColumn(logoBox, 0);
        shell.Children.Add(logoBox);

        _profileTitle = CreateTextBlock(11, FontWeights.SemiBold);
        _profileTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _profilePlan = CreateTextBlock(9, FontWeights.Normal);
        _profilePlan.Margin = new Thickness(0, -1, 0, 0);
        _profilePlan.TextTrimming = TextTrimming.CharacterEllipsis;
        StackPanel profile = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _profileTitle, _profilePlan }
        };
        Grid.SetColumn(profile, 2);
        shell.Children.Add(profile);

        Grid quotas = new() { VerticalAlignment = VerticalAlignment.Center };
        quotas.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _weeklySpacer = new RowDefinition { Height = new GridLength(3) };
        quotas.RowDefinitions.Add(_weeklySpacer);
        quotas.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        (_primaryLabel, _primaryText, _primaryTrack, _primaryFill, _primaryFillColumn, _primaryRestColumn, Grid primaryRow) =
            CreateQuotaRow();
        Grid.SetRow(primaryRow, 0);
        quotas.Children.Add(primaryRow);

        (_weeklyLabel, _weeklyText, _weeklyTrack, _weeklyFill, _weeklyFillColumn, _weeklyRestColumn, _weeklyRow) =
            CreateQuotaRow();
        Grid.SetRow(_weeklyRow, 2);
        quotas.Children.Add(_weeklyRow);
        Grid.SetColumn(quotas, 4);
        shell.Children.Add(quotas);

        _pill.Child = shell;
        _canvas.Children.Add(_pill);

        _window = new Window
        {
            Title = "Fluent AgentBar Widget",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Left = -32000,
            Top = -32000,
            Width = TaskbarWidgetLayoutCalculator.WidgetLogicalWidth,
            Height = TaskbarWidgetLayoutCalculator.WidgetLogicalHeight,
            Content = _canvas
        };
        _window.Closed += OnWindowClosed;

        _watchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _watchdog.Tick += OnWatchdogTick;

        ApplyVisualState(initialState);
    }

    internal TaskbarWidgetRuntimeInfo RuntimeInfo { get; private set; } =
        new(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, default, TaskbarWidgetMode.Child, null);

    internal void Start()
    {
        if (!NativeMethods.HasExpectedTaskbarClass(_taskbarHwnd, _isPrimary))
        {
            throw new InvalidOperationException("The requested taskbar HWND is no longer current.");
        }

        _window.Show();
        _window.UpdateLayout();
        _hwnd = new WindowInteropHelper(_window).Handle;
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("WPF did not create a widget HWND.");
        }

        _originalStyle = NativeMethods.GetStyle(_hwnd, NativeMethods.GwlStyle);
        _originalExStyle = NativeMethods.GetStyle(_hwnd, NativeMethods.GwlExStyle);
        long exStyle = (_originalExStyle | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow) &
            ~NativeMethods.WsExAppWindow;
        NativeMethods.SetStyle(_hwnd, NativeMethods.GwlExStyle, exStyle, "Setting widget extended styles failed.");

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WindowHook);
        CreateMessageWindow();
        AdoptOrFallback();
        _watchdog.Start();
    }

    internal void Update(WidgetVisualState state, IReadOnlyList<WidgetMenuEntry> menuEntries)
    {
        _state = state;
        _menuEntries = menuEntries;
        ApplyVisualState(state);
        Reposition();
    }

    internal void Reposition()
    {
        if (_closing)
        {
            return;
        }

        if (!NativeMethods.HasExpectedTaskbarClass(_taskbarHwnd, _isPrimary) ||
            _hwnd == IntPtr.Zero ||
            !NativeMethods.IsWindow(_hwnd))
        {
            ReportTargetLost();
            return;
        }

        if (_mode == TaskbarWidgetMode.Child && NativeMethods.GetParent(_hwnd) != _taskbarHwnd)
        {
            ReportTargetLost();
            return;
        }

        TaskbarWidgetLayout layout = CalculateCurrentLayout();
        if (_mode == TaskbarWidgetMode.Child)
        {
            PositionAsChild(layout);
        }
        else if (!NativeMethods.IsForegroundWindowFullscreenOnMonitor(_taskbarHwnd))
        {
            PositionAsFallback(layout);
        }

        UpdateRuntimeInfo(layout);
    }

    internal void Close()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _watchdog.Stop();
        _watchdog.Tick -= OnWatchdogTick;

        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
            NativeMethods.ClearModeProperty(_hwnd);
            _ = NativeMethods.SetParent(_hwnd, IntPtr.Zero);
            NativeMethods.SetStyle(_hwnd, NativeMethods.GwlStyle, _originalStyle, "Restoring widget style during teardown failed.");
            NativeMethods.SetOwner(_hwnd, IntPtr.Zero);
        }

        _source?.RemoveHook(WindowHook);
        _source = null;
        _messageSource?.RemoveHook(MessageWindowHook);
        _messageSource?.Dispose();
        _messageSource = null;

        try
        {
            _window.Close();
        }
        catch (InvalidOperationException)
        {
            // Explorer may already have destroyed the child HWND.
        }
    }

    private void AdoptOrFallback()
    {
        string? childFailure = null;
        try
        {
            TaskbarWidgetLayout layout = CalculateCurrentLayout();
            if (!TaskbarWidgetLayoutCalculator.CanHostAsChild(layout))
            {
                throw new InvalidOperationException(
                    "The taskbar cannot contain the complete widget pill in WS_CHILD mode.");
            }

            long childStyle = (_originalStyle & ~NativeMethods.WsPopup) | NativeMethods.WsChild | NativeMethods.WsVisible;
            NativeMethods.SetStyle(_hwnd, NativeMethods.GwlStyle, childStyle, "SetWindowLongPtr(GWL_STYLE) rejected WS_CHILD.");
            long styleAfter = NativeMethods.GetStyle(_hwnd, NativeMethods.GwlStyle);
            if ((styleAfter & NativeMethods.WsChild) == 0 || (styleAfter & NativeMethods.WsPopup) != 0)
            {
                throw new Win32Exception("The widget HWND did not retain the required WS_CHILD/WS_POPUP style transition.");
            }

            _ = NativeMethods.SetParent(_hwnd, _taskbarHwnd);
            if (NativeMethods.GetParent(_hwnd) != _taskbarHwnd)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "SetParent did not attach the WPF HWND to the taskbar.");
            }

            _mode = TaskbarWidgetMode.Child;
            PositionAsChild(layout);
            NativeMethods.SetModeProperty(_hwnd, _mode);
            UpdateRuntimeInfo(layout);
            _log?.Invoke(
                $"Taskbar widget HWND=0x{_hwnd.ToInt64():X} is active in WS_CHILD mode; " +
                $"taskbar=0x{_taskbarHwnd.ToInt64():X}; DPI={layout.Dpi}; pill={layout.PillScreenRect}.");
            return;
        }
        catch (Exception ex)
        {
            childFailure = ex.ToString();
            _log?.Invoke(
                $"WS_CHILD setup failed for taskbar 0x{_taskbarHwnd.ToInt64():X}; " +
                $"activating owned-window fallback on the same WPF HWND. {ex}");
        }

        _childModeFailure = childFailure;
        ActivateFallback();
    }

    private void ActivateFallback()
    {
        _ = NativeMethods.SetParent(_hwnd, IntPtr.Zero);
        long popupStyle = (_originalStyle & ~NativeMethods.WsChild) | NativeMethods.WsPopup | NativeMethods.WsVisible;
        NativeMethods.SetStyle(_hwnd, NativeMethods.GwlStyle, popupStyle, "Restoring WS_POPUP for fallback failed.");
        long styleAfter = NativeMethods.GetStyle(_hwnd, NativeMethods.GwlStyle);
        if ((styleAfter & NativeMethods.WsChild) != 0)
        {
            throw new Win32Exception("The fallback widget still has WS_CHILD after style restoration.");
        }

        NativeMethods.SetOwner(_hwnd, _taskbarHwnd);
        _mode = TaskbarWidgetMode.OwnedFallback;
        TaskbarWidgetLayout layout = CalculateCurrentLayout();
        PositionAsFallback(layout);
        NativeMethods.SetModeProperty(_hwnd, _mode);
        UpdateRuntimeInfo(layout);
        _log?.Invoke(
            $"Taskbar widget HWND=0x{_hwnd.ToInt64():X} is active in owned topmost fallback mode; " +
            $"taskbar=0x{_taskbarHwnd.ToInt64():X}; DPI={layout.Dpi}; pill={layout.PillScreenRect}.");
    }

    private TaskbarWidgetLayout CalculateCurrentLayout()
    {
        NativeRect taskbarRect = NativeMethods.GetRequiredRect(_taskbarHwnd, "taskbar");
        _trayNotifyHwnd = NativeMethods.FindWindowEx(_taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
        NativeRect trayRect = _trayNotifyHwnd != IntPtr.Zero && NativeMethods.IsWindow(_trayNotifyHwnd)
            ? NativeMethods.GetRequiredRect(_trayNotifyHwnd, "TrayNotifyWnd")
            : taskbarRect;
        uint dpi = NativeMethods.GetDpiForWindow(_taskbarHwnd);
        return TaskbarWidgetLayoutCalculator.Calculate(taskbarRect, trayRect, dpi);
    }

    private void PositionAsChild(TaskbarWidgetLayout layout)
    {
        double scale = Math.Max(1.0, layout.Dpi / 96d);
        _window.Width = layout.TaskbarScreenRect.Width / scale;
        _window.Height = layout.TaskbarScreenRect.Height / scale;
        Canvas.SetLeft(_pill, layout.PillRegionRect.Left / scale);
        Canvas.SetTop(_pill, layout.PillRegionRect.Top / scale);

        NativeMethods.Point topLeft = new(layout.TaskbarScreenRect.Left, layout.TaskbarScreenRect.Top);
        NativeMethods.Point bottomRight = new(layout.TaskbarScreenRect.Right, layout.TaskbarScreenRect.Bottom);
        if (!NativeMethods.ScreenToClient(_taskbarHwnd, ref topLeft) ||
            !NativeMethods.ScreenToClient(_taskbarHwnd, ref bottomRight))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ScreenToClient failed for taskbar child geometry.");
        }

        if (!NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                topLeft.X,
                topLeft.Y,
                bottomRight.X - topLeft.X,
                bottomRight.Y - topLeft.Y,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpFrameChanged |
                NativeMethods.SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed for taskbar child mode.");
        }

        NativeMethods.ApplyRoundedRegion(_hwnd, layout.PillRegionRect, layout.Dpi);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShowNoActivate);
    }

    private void PositionAsFallback(TaskbarWidgetLayout layout)
    {
        _window.Width = TaskbarWidgetLayoutCalculator.WidgetLogicalWidth;
        _window.Height = TaskbarWidgetLayoutCalculator.WidgetLogicalHeight;
        Canvas.SetLeft(_pill, 0);
        Canvas.SetTop(_pill, 0);

        if (!NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HwndTopMost,
                layout.PillScreenRect.Left,
                layout.PillScreenRect.Top,
                layout.PillScreenRect.Width,
                layout.PillScreenRect.Height,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpFrameChanged |
                NativeMethods.SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed for owned fallback mode.");
        }

        NativeMethods.ApplyRoundedRegion(
            _hwnd,
            new NativeRect(0, 0, layout.PillScreenRect.Width, layout.PillScreenRect.Height),
            layout.Dpi);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShowNoActivate);
    }

    private void ApplyVisualState(WidgetVisualState state)
    {
        _profileTitle.Text = state.ProfileTitle;
        _profilePlan.Text = state.ProfilePlan;
        _profilePlan.Visibility = state.ProfilePlan.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pill.ToolTip = state.ProfileLabel;
        _primaryLabel.Text = state.PrimaryQuotaLabel;
        _primaryText.Text = state.PrimaryRemainingText;
        _weeklyLabel.Text = state.WeeklyQuotaLabel;
        _weeklyText.Text = state.WeeklyRemainingText;
        _weeklyRow.Visibility = state.HasWeeklyQuota ? Visibility.Visible : Visibility.Collapsed;
        _weeklySpacer.Height = state.HasWeeklyQuota ? new GridLength(3) : new GridLength(0);

        SetFillColumns(_primaryFillColumn, _primaryRestColumn, state.PrimaryRemainingPercent);
        SetFillColumns(_weeklyFillColumn, _weeklyRestColumn, state.WeeklyRemainingPercent);

        Color primaryText = state.IsDarkTaskbar
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(228, 0, 0, 0);
        Color secondaryText = state.IsDarkTaskbar
            ? Color.FromArgb(198, 255, 255, 255)
            : Color.FromArgb(158, 0, 0, 0);
        Color track = state.IsDarkTaskbar
            ? Color.FromArgb(48, 255, 255, 255)
            : Color.FromArgb(40, 0, 0, 0);

        _profileTitle.Foreground = new SolidColorBrush(primaryText);
        _primaryText.Foreground = new SolidColorBrush(primaryText);
        _weeklyText.Foreground = new SolidColorBrush(primaryText);
        _profilePlan.Foreground = new SolidColorBrush(secondaryText);
        _primaryLabel.Foreground = new SolidColorBrush(secondaryText);
        _weeklyLabel.Foreground = new SolidColorBrush(secondaryText);
        _primaryTrack.Background = new SolidColorBrush(track);
        _weeklyTrack.Background = new SolidColorBrush(track);
        _primaryFill.Background = new SolidColorBrush(StatusColor(state.PrimaryRemainingPercent, state.IsDarkTaskbar));
        _weeklyFill.Background = new SolidColorBrush(StatusColor(state.WeeklyRemainingPercent, state.IsDarkTaskbar));

        bool claude = IsClaude(state.ProviderName);
        _logo.Data = Geometry.Parse(claude ? ProviderPathData.Claude : ProviderPathData.OpenAi);
        _logo.Fill = new SolidColorBrush(
            claude
                ? Color.FromArgb(255, 217, 119, 87)
                : primaryText);
        _pill.Background = CreateTintBrush(state.ProviderName, state.IsDarkTaskbar, state.IsGlowEnabled);
    }

    private static TextBlock CreateTextBlock(double fontSize, FontWeight weight) =>
        new()
        {
            FontFamily = TextFont,
            FontSize = fontSize,
            FontWeight = weight,
            VerticalAlignment = VerticalAlignment.Center
        };

    private static (
        TextBlock Label,
        TextBlock Value,
        Border Track,
        Border Fill,
        ColumnDefinition FillColumn,
        ColumnDefinition RestColumn,
        Grid Row) CreateQuotaRow()
    {
        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        TextBlock label = CreateTextBlock(11, FontWeights.Normal);
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        Border track = new() { Height = 5, CornerRadius = new CornerRadius(2.5) };
        Grid fillGrid = new();
        ColumnDefinition fillColumn = new();
        ColumnDefinition restColumn = new();
        fillGrid.ColumnDefinitions.Add(fillColumn);
        fillGrid.ColumnDefinitions.Add(restColumn);
        Border fill = new() { CornerRadius = new CornerRadius(2.5) };
        fillGrid.Children.Add(fill);
        Grid trackHost = new()
        {
            Height = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { track, fillGrid }
        };
        Grid.SetColumn(trackHost, 2);
        row.Children.Add(trackHost);

        TextBlock value = CreateTextBlock(11, FontWeights.SemiBold);
        value.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(value, 4);
        row.Children.Add(value);
        return (label, value, track, fill, fillColumn, restColumn, row);
    }

    private static void SetFillColumns(
        ColumnDefinition fill,
        ColumnDefinition rest,
        int remainingPercent)
    {
        int clamped = Math.Clamp(remainingPercent, 0, 100);
        fill.Width = new GridLength(clamped, GridUnitType.Star);
        rest.Width = new GridLength(100 - clamped, GridUnitType.Star);
    }

    private static Brush CreateTintBrush(string providerName, bool dark, bool enabled)
    {
        if (!enabled)
        {
            return Brushes.Transparent;
        }

        bool claude = IsClaude(providerName);
        Color brand = claude
            ? Color.FromArgb(255, 217, 119, 87)
            : dark
                ? Color.FromArgb(255, 96, 205, 255)
                : Color.FromArgb(255, 0, 103, 192);
        byte alpha = (byte)(dark ? 34 : 26);
        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(alpha, brand.R, brand.G, brand.B), 0),
                new(Color.FromArgb((byte)(alpha / 2), brand.R, brand.G, brand.B), 0.45),
                new(Color.FromArgb(0, brand.R, brand.G, brand.B), 1)
            },
            new Point(0, 0),
            new Point(1, 0));
    }

    private static Color StatusColor(int remainingPercent, bool dark)
    {
        if (remainingPercent <= 15)
        {
            return dark
                ? Color.FromArgb(255, 255, 153, 164)
                : Color.FromArgb(255, 196, 43, 28);
        }

        if (remainingPercent <= 30)
        {
            return dark
                ? Color.FromArgb(255, 252, 225, 0)
                : Color.FromArgb(255, 157, 93, 0);
        }

        return dark
            ? Color.FromArgb(255, 96, 205, 255)
            : Color.FromArgb(255, 0, 103, 192);
    }

    private static bool IsClaude(string providerName) =>
        providerName.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
        providerName.Contains("anthropic", StringComparison.OrdinalIgnoreCase);

    private void ShowContextMenu()
    {
        ContextMenu menu = CreateContextMenu();
        _pill.ContextMenu = menu;
        menu.PlacementTarget = _pill;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private ContextMenu CreateContextMenu()
    {
        bool dark = _state.IsDarkTaskbar;
        ContextMenu menu = new()
        {
            FontFamily = TextFont,
            FontSize = 12,
            Background = new SolidColorBrush(dark
                ? Color.FromRgb(43, 43, 48)
                : Color.FromRgb(251, 251, 251)),
            Foreground = new SolidColorBrush(dark
                ? Color.FromRgb(255, 255, 255)
                : Color.FromArgb(228, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(dark
                ? Color.FromRgb(70, 70, 76)
                : Color.FromRgb(218, 218, 218)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };

        foreach (WidgetMenuEntry entry in _menuEntries)
        {
            AddMenuEntry(menu.Items, entry);
        }

        return menu;
    }

    private void AddMenuEntry(ItemCollection items, WidgetMenuEntry entry)
    {
        if (entry.IsSeparator)
        {
            items.Add(new Separator());
            return;
        }

        MenuItem item = new()
        {
            Header = entry.Text,
            IsEnabled = entry.IsEnabled
        };
        string? glyph = entry.IsChecked ? "\uE73E" : entry.Glyph;
        if (!string.IsNullOrEmpty(glyph))
        {
            item.Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        if (entry.Children is { Count: > 0 })
        {
            foreach (WidgetMenuEntry child in entry.Children)
            {
                AddMenuEntry(item.Items, child);
            }
        }
        else if (!string.IsNullOrEmpty(entry.CommandId))
        {
            item.Click += (_, _) => _commandInvoked(entry.CommandId);
        }

        items.Add(item);
    }

    private void CreateMessageWindow()
    {
        HwndSourceParameters parameters = new("FluentAgentBar.TaskbarMessageWindow")
        {
            WindowStyle = unchecked((int)NativeMethods.WsPopup),
            ExtendedWindowStyle = unchecked((int)(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate)),
            PositionX = -32000,
            PositionY = -32000,
            Width = 0,
            Height = 0
        };
        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(MessageWindowHook);
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    }

    private IntPtr MessageWindowHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            ReportTargetLost();
        }
        else if (message is NativeMethods.WmDisplayChange or NativeMethods.WmSettingChange)
        {
            QueueReposition();
        }

        return IntPtr.Zero;
    }

    private IntPtr WindowHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == NativeMethods.WmMouseActivate)
        {
            handled = true;
            return new IntPtr(NativeMethods.MaNoActivate);
        }

        if (message == NativeMethods.WmLeftButtonUp && IsPointInsidePill(lParam))
        {
            handled = true;
            _usageRequested();
        }
        else if (message == NativeMethods.WmRightButtonUp && IsPointInsidePill(lParam))
        {
            handled = true;
            ShowContextMenu();
        }
        else if (message is NativeMethods.WmDpiChanged or NativeMethods.WmDisplayChange)
        {
            QueueReposition();
        }
        else if (message == NativeMethods.WmNcDestroy && !_closing)
        {
            ReportTargetLost();
        }
        else if (message == NativeMethods.DebugExitMessage && DesktopTestHook.IsEnabled)
        {
            handled = true;
            _commandInvoked("exit");
        }
        else if (message == NativeMethods.DebugDetachMessage && DesktopTestHook.IsEnabled)
        {
            handled = true;
            _ = NativeMethods.SetParent(_hwnd, IntPtr.Zero);
        }

        return IntPtr.Zero;
    }

    private bool IsPointInsidePill(IntPtr lParam)
    {
        int packed = unchecked((int)lParam.ToInt64());
        int x = unchecked((short)(packed & 0xFFFF));
        int y = unchecked((short)((packed >> 16) & 0xFFFF));
        NativeRect hitRect = _mode == TaskbarWidgetMode.Child
            ? RuntimeInfo.Layout.PillRegionRect
            : new NativeRect(
                0,
                0,
                RuntimeInfo.Layout.PillScreenRect.Width,
                RuntimeInfo.Layout.PillScreenRect.Height);
        return
            x >= hitRect.Left &&
            x < hitRect.Right &&
            y >= hitRect.Top &&
            y < hitRect.Bottom;
    }

    private void QueueReposition()
    {
        _window.Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
    }

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        try
        {
            Reposition();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Taskbar widget stability check failed: {ex}");
            ReportTargetLost();
        }
    }

    private void ReportTargetLost()
    {
        if (_closing || _targetLostReported)
        {
            return;
        }

        _targetLostReported = true;
        _targetLost();
    }

    private void UpdateRuntimeInfo(TaskbarWidgetLayout layout)
    {
        RuntimeInfo = new TaskbarWidgetRuntimeInfo(
            _hwnd,
            _taskbarHwnd,
            _trayNotifyHwnd,
            layout,
            _mode,
            _childModeFailure);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (!_closing)
        {
            _closedUnexpectedly();
        }
    }
}
