using FluentAgentBar.WpfTaskbarWidget;
using Microsoft.UI.Dispatching;

namespace FluentAgentBar;

internal sealed class TaskbarWidgetHost : IDisposable
{
    private const string RefreshCommand = "refresh";
    private const string SettingsCommand = "settings";
    private const string OpenConfigCommand = "open-config";
    private const string StartupCommand = "toggle-startup";
    private const string GlowCommand = "toggle-glow";
    private const string AcrylicCommand = "toggle-acrylic";
    private const string ExitCommand = "exit";
    private const string SwitchCommandPrefix = "switch-codex:";
    private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(6);

    private readonly UsageService _usageService;
    private readonly TaskbarTarget _target;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _cycleTimer;
    private readonly Dictionary<string, ProfileConfig> _switchProfiles = [];
    private WpfTaskbarWidgetController? _controller;
    private int _cycleIndex;
    private bool _closed;

    internal TaskbarWidgetHost(UsageService usageService, TaskbarTarget target)
    {
        _usageService = usageService;
        _target = target;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _cycleTimer = _dispatcherQueue.CreateTimer();
        _cycleTimer.Interval = CycleInterval;
        _cycleTimer.Tick += OnCycleTimerTick;
    }

    public event EventHandler? UsageRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? TargetLost;
    public event EventHandler? Closed;

    internal IntPtr TargetHwnd => _target.Hwnd;

    internal void ShowNoActivate()
    {
        if (_controller is not null)
        {
            return;
        }

        AppConfig config = AppConfigStore.Load();
        WidgetVisualState state = BuildVisualState(config);
        IReadOnlyList<WidgetMenuEntry> menu = BuildMenu(config);
        _controller = WpfTaskbarWidgetController.StartAsync(
                _target.Hwnd,
                _target.IsPrimary,
                state,
                menu,
                message => System.Diagnostics.Trace.WriteLine($"[WPF taskbar widget] {message}"))
            .GetAwaiter()
            .GetResult();
        _controller.UsageRequested += OnControllerUsageRequested;
        _controller.CommandInvoked += OnControllerCommandInvoked;
        _controller.TargetLost += OnControllerTargetLost;
        _controller.Closed += OnControllerClosed;

        AppConfigStore.Changed += OnConfigChanged;
        _usageService.Updated += OnUsageUpdated;
        _cycleTimer.Start();
    }

    internal bool TryGetPillScreenRect(out NativeMethods.Rect rect)
    {
        rect = default;
        if (_controller is null)
        {
            return false;
        }

        try
        {
            NativeRect pill = _controller.GetRuntimeInfo().Layout.PillScreenRect;
            rect = new NativeMethods.Rect
            {
                Left = pill.Left,
                Top = pill.Top,
                Right = pill.Right,
                Bottom = pill.Bottom
            };
            return !pill.IsEmpty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    internal void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _cycleTimer.Stop();
        _cycleTimer.Tick -= OnCycleTimerTick;
        AppConfigStore.Changed -= OnConfigChanged;
        _usageService.Updated -= OnUsageUpdated;

        if (_controller is not null)
        {
            _controller.UsageRequested -= OnControllerUsageRequested;
            _controller.CommandInvoked -= OnControllerCommandInvoked;
            _controller.TargetLost -= OnControllerTargetLost;
            _controller.Closed -= OnControllerClosed;
            try
            {
                _controller.CloseAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            _controller = null;
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(Refresh);
    }

    private void OnUsageUpdated(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(Refresh);
    }

    private void OnCycleTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (BuildEntries(AppConfigStore.Load()).Count > 1)
        {
            _cycleIndex++;
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_closed || _controller is null)
        {
            return;
        }

        AppConfig config = AppConfigStore.Load();
        _controller.Update(BuildVisualState(config), BuildMenu(config));
    }

    private WidgetVisualState BuildVisualState(AppConfig config)
    {
        IReadOnlyList<(string Provider, ProfileUsage Profile)> entries = BuildEntries(config);
        (string providerName, ProfileUsage profile) =
            entries[((_cycleIndex % entries.Count) + entries.Count) % entries.Count];
        IReadOnlyList<QuotaWindowUsage> quotaWindows =
            profile.DisplayQuotaGroups.FirstOrDefault()?.Windows ?? [];
        QuotaWindowUsage? primaryQuota = quotaWindows.ElementAtOrDefault(0);
        QuotaWindowUsage? weeklyQuota = quotaWindows.ElementAtOrDefault(1);
        return new WidgetVisualState(
            providerName,
            profile.Label,
            profile.PlanDisplay,
            $"{providerName} \u00B7 {profile.Label}",
            primaryQuota?.Label ?? profile.PrimaryQuotaLabel,
            primaryQuota?.RemainingPercent ?? profile.RemainingPercent,
            primaryQuota?.RemainingText ?? profile.RemainingText,
            weeklyQuota?.Label == "Weekly" ? "Wk" : weeklyQuota?.Label ?? "Wk",
            weeklyQuota?.RemainingPercent ?? 0,
            weeklyQuota?.RemainingText ?? "--",
            weeklyQuota is not null,
            TaskbarTheme.IsDark(),
            config.WidgetGlowEnabled);
    }

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

    private IReadOnlyList<WidgetMenuEntry> BuildMenu(AppConfig config)
    {
        _switchProfiles.Clear();
        List<WidgetMenuEntry> switchChildren = [];
        List<ProfileConfig> profiles = config.Profiles
            .Where(profile => AppConfigStore.IsProvider(profile, "codex") && profile.Enabled)
            .ToList();
        if (profiles.Count == 0)
        {
            switchChildren.Add(new WidgetMenuEntry(
                string.Empty,
                "No enabled Codex profiles",
                IsEnabled: false));
        }
        else
        {
            for (int index = 0; index < profiles.Count; index++)
            {
                ProfileConfig profile = profiles[index];
                bool hasAuth = CodexAccountSwitchService.HasProfileAuth(profile);
                bool active = hasAuth && CodexAccountSwitchService.IsActiveProfile(profile);
                string command = $"{SwitchCommandPrefix}{index}";
                _switchProfiles[command] = new ProfileConfig
                {
                    Provider = profile.Provider,
                    Label = profile.Label,
                    Home = profile.Home,
                    Enabled = profile.Enabled
                };
                switchChildren.Add(new WidgetMenuEntry(
                    command,
                    active
                        ? $"{profile.Label} (active)"
                        : hasAuth
                            ? profile.Label
                            : $"{profile.Label} (login needed)",
                    active ? "\uE73E" : "\uE8AB",
                    IsEnabled: hasAuth && !active));
            }
        }

        return
        [
            new WidgetMenuEntry(RefreshCommand, "Refresh now", "\uE72C"),
            new WidgetMenuEntry(
                string.Empty,
                "Switch Codex account",
                "\uE8AB",
                Children: switchChildren),
            new WidgetMenuEntry(SettingsCommand, "Settings", "\uE713"),
            new WidgetMenuEntry(OpenConfigCommand, "Open config file", "\uE8A5"),
            WidgetMenuEntry.Separator(),
            new WidgetMenuEntry(
                StartupCommand,
                "Start with Windows",
                IsChecked: WindowsStartupService.IsEnabled()),
            new WidgetMenuEntry(
                GlowCommand,
                "Widget glow",
                IsChecked: config.WidgetGlowEnabled),
            new WidgetMenuEntry(
                AcrylicCommand,
                "Acrylic flyout",
                IsChecked: string.Equals(config.FlyoutStyle, "acrylic", StringComparison.OrdinalIgnoreCase)),
            WidgetMenuEntry.Separator(),
            new WidgetMenuEntry(ExitCommand, "Exit", "\uE8BB")
        ];
    }

    private void OnControllerUsageRequested(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() => UsageRequested?.Invoke(this, EventArgs.Empty));
    }

    private void OnControllerCommandInvoked(object? sender, string commandId)
    {
        _dispatcherQueue.TryEnqueue(() => HandleCommand(commandId));
    }

    private void OnControllerTargetLost(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() => TargetLost?.Invoke(this, EventArgs.Empty));
    }

    private void OnControllerClosed(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() => Closed?.Invoke(this, EventArgs.Empty));
    }

    private void HandleCommand(string commandId)
    {
        switch (commandId)
        {
            case RefreshCommand:
                _ = _usageService.FetchAsync();
                break;
            case SettingsCommand:
                SettingsWindow.ShowInstance();
                break;
            case OpenConfigCommand:
                OpenConfigFile();
                break;
            case StartupCommand:
                ToggleStartup();
                break;
            case GlowCommand:
                ToggleGlow();
                break;
            case AcrylicCommand:
                ToggleAcrylic();
                break;
            case ExitCommand:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                if (_switchProfiles.TryGetValue(commandId, out ProfileConfig? profile))
                {
                    _ = SwitchCodexProfileFromMenuAsync(profile);
                }

                break;
        }
    }

    private async Task SwitchCodexProfileFromMenuAsync(ProfileConfig profile)
    {
        try
        {
            await CodexAccountSwitchService.SwitchToProfileAsync(profile);
            await _usageService.FetchAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private static void OpenConfigFile()
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

    private static void ToggleStartup()
    {
        bool requested = !WindowsStartupService.IsEnabled();
        if (!WindowsStartupService.TrySetEnabled(requested, out string errorMessage))
        {
            System.Diagnostics.Debug.WriteLine(errorMessage);
        }
    }

    private static void ToggleGlow()
    {
        AppConfig current = AppConfigStore.Load();
        current.WidgetGlowEnabled = !current.WidgetGlowEnabled;
        AppConfigStore.Save(current);
    }

    private static void ToggleAcrylic()
    {
        AppConfig current = AppConfigStore.Load();
        current.FlyoutStyle = string.Equals(current.FlyoutStyle, "acrylic", StringComparison.OrdinalIgnoreCase)
            ? "solid"
            : "acrylic";
        AppConfigStore.Save(current);
    }
}
