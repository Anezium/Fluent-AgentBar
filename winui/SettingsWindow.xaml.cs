using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls.Primitives;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace FluentAgentBar;

public sealed partial class SettingsWindow : Window
{
    private const int WindowLogicalWidth = 920;
    private const int WindowLogicalHeight = 680;
    private const double ContentSideMargin = 40;
    private const double MinimumContentWidth = 520;

    private static SettingsWindow? _instance;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private bool _isApplyingConfig;

    public SettingsWindow()
    {
        InitializeComponent();

        ContentPanel.Loaded += OnContentPanelLoaded;
        SettingsScroll.SizeChanged += OnSettingsScrollSizeChanged;

        Title = "Fluent AgentBar Settings";
        SystemBackdrop = new MicaBackdrop();

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        NativeMethods.SetImmersiveDarkMode(_hwnd, ShouldUseDarkTitleBar());
        ConfigureWindow();
        ApplyConfig(AppConfigStore.Load());
        AppConfigStore.Changed += OnConfigChanged;
        WindowsStartupService.Changed += OnStartupChanged;
        Closed += OnClosed;
    }

    private void OnContentPanelLoaded(object sender, RoutedEventArgs e)
    {
        UpdateContentPanelWidth();
    }

    private void OnSettingsScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateContentPanelWidth();
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => ApplyConfig(AppConfigStore.Load()));
    }

    private void OnStartupChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(ApplyStartupState);
    }

    private void ApplyConfig(AppConfig config)
    {
        _isApplyingConfig = true;
        try
        {
            RefreshIntervalNumberBox.Value = Math.Clamp(config.RefreshIntervalSeconds, 30, 3600);
            StartupToggle.IsOn = WindowsStartupService.IsEnabled();
            WidgetGlowToggle.IsOn = config.WidgetGlowEnabled;
            BackdropComboBox.SelectedIndex =
                string.Equals(config.FlyoutStyle, "solid", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        finally
        {
            _isApplyingConfig = false;
        }

        RebuildProfileCards(config);
    }

    private void ApplyStartupState()
    {
        _isApplyingConfig = true;
        try
        {
            StartupToggle.IsOn = WindowsStartupService.IsEnabled();
        }
        finally
        {
            _isApplyingConfig = false;
        }
    }

    private void RebuildProfileCards(AppConfig config)
    {
        ProfilesPanel.Children.Clear();
        for (int i = 0; i < config.Profiles.Count; i++)
        {
            ProfilesPanel.Children.Add(CreateProfileCard(config, i));
        }
    }

    private CommunityToolkit.WinUI.Controls.SettingsCard CreateProfileCard(AppConfig config, int profileIndex)
    {
        ProfileConfig profile = config.Profiles[profileIndex];
        bool canRemove = config.Profiles.Count > 1;

        ToggleSwitch enabledToggle = new()
        {
            IsOn = profile.Enabled,
            OffContent = "",
            OnContent = ""
        };
        ToolTipService.SetToolTip(enabledToggle, "Enabled");
        enabledToggle.Toggled += (_, _) =>
        {
            if (_isApplyingConfig)
            {
                return;
            }

            AppConfig current = AppConfigStore.Load();
            if (profileIndex < current.Profiles.Count &&
                current.Profiles[profileIndex].Enabled != enabledToggle.IsOn)
            {
                current.Profiles[profileIndex].Enabled = enabledToggle.IsOn;
                AppConfigStore.Save(current);
            }
        };

        Button loginButton = new() { MinWidth = 72, Content = "Login" };
        loginButton.Click += async (_, _) => await StartProfileLoginAsync(profileIndex);

        Button renameButton = new() { MinWidth = 80, Content = "Rename" };
        renameButton.Click += async (_, _) => await RenameProfileAsync(profileIndex);

        Button removeButton = new()
        {
            Content = new FontIcon { FontSize = 14, Glyph = "" },
            IsEnabled = canRemove
        };
        ToolTipService.SetToolTip(removeButton, canRemove ? "Remove profile" : "The last profile cannot be removed");
        removeButton.Click += async (_, _) => await RemoveProfileAsync(profileIndex);

        StackPanel actions = new()
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        actions.Children.Add(enabledToggle);
        actions.Children.Add(loginButton);
        actions.Children.Add(renameButton);
        actions.Children.Add(removeButton);

        string providerName = AppConfigStore.IsProvider(profile, "claude") ? "Claude" : "Codex";
        return new CommunityToolkit.WinUI.Controls.SettingsCard
        {
            Header = profile.Label,
            Description = $"{providerName} · {AppConfigStore.ProfilePathLabel(profile.Home)}",
            HeaderIcon = new PathIcon
            {
                Data = ProviderIcons.IconGeometryFor(providerName, 16),
                Foreground = ProviderIcons.BrushForTheme(providerName, Root.ActualTheme)
            },
            Content = actions
        };
    }

    private async void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        (string provider, string label)? choice = await PromptForNewProfileAsync();
        if (choice is not { } newProfile)
        {
            return;
        }

        AppConfig config = AppConfigStore.Load();
        if (config.Profiles.Any(profile =>
                AppConfigStore.IsProvider(profile, newProfile.provider) &&
                string.Equals(profile.Label, newProfile.label, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowMessageAsync(
                "Profile exists",
                $"A {newProfile.provider} profile named \"{newProfile.label}\" already exists.");
            return;
        }

        string home = AppConfigStore.DefaultHomeForLabel(newProfile.provider, newProfile.label);
        try
        {
            Directory.CreateDirectory(Environment.ExpandEnvironmentVariables(home));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not create profile folder", ex.Message);
            return;
        }

        config.Profiles.Add(new ProfileConfig
        {
            Provider = newProfile.provider,
            Label = newProfile.label,
            Home = home,
            Enabled = true
        });
        AppConfigStore.Save(config);
    }

    private async Task<(string provider, string label)?> PromptForNewProfileAsync()
    {
        ToggleButton codexTile = CreateProviderTile("Codex");
        ToggleButton claudeTile = CreateProviderTile("Claude");
        codexTile.IsChecked = true;

        // Radio-style behavior: exactly one tile stays selected.
        codexTile.Click += (_, _) =>
        {
            codexTile.IsChecked = true;
            claudeTile.IsChecked = false;
        };
        claudeTile.Click += (_, _) =>
        {
            claudeTile.IsChecked = true;
            codexTile.IsChecked = false;
        };

        StackPanel tiles = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8
        };
        tiles.Children.Add(codexTile);
        tiles.Children.Add(claudeTile);

        TextBox nameBox = new()
        {
            PlaceholderText = "Profile name",
            MinWidth = 280
        };

        StackPanel content = new() { Spacing = 16, MinWidth = 296 };
        content.Children.Add(tiles);
        content.Children.Add(nameBox);

        ContentDialog dialog = new()
        {
            Title = "New profile",
            Content = content,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = Root.XamlRoot
        };

        nameBox.TextChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text);
        };
        dialog.Opened += (_, _) => nameBox.Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return null;
        }

        string provider = claudeTile.IsChecked == true ? "claude" : "codex";
        return (provider, nameBox.Text.Trim());
    }

    private ToggleButton CreateProviderTile(string providerName)
    {
        Viewbox logo = new()
        {
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new XamlPath
            {
                Data = ProviderIcons.GeometryFor(providerName),
                Fill = ProviderIcons.BrushForTheme(providerName, Root.ActualTheme)
            }
        };

        StackPanel tileContent = new()
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        tileContent.Children.Add(logo);
        tileContent.Children.Add(new TextBlock
        {
            Text = providerName,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        return new ToggleButton
        {
            Content = tileContent,
            MinWidth = 140,
            Padding = new Thickness(12, 14, 12, 12)
        };
    }

    private async Task RenameProfileAsync(int profileIndex)
    {
        AppConfig config = AppConfigStore.Load();
        if (profileIndex >= config.Profiles.Count)
        {
            return;
        }

        ProfileConfig profile = config.Profiles[profileIndex];
        string currentName = profile.Label;
        string? newName = await PromptForProfileNameAsync("Rename profile", currentName, "Rename");
        if (!string.IsNullOrWhiteSpace(newName) && newName.Trim() != currentName)
        {
            profile.Label = newName.Trim();
            AppConfigStore.Save(config);
        }
    }

    private async Task RemoveProfileAsync(int profileIndex)
    {
        AppConfig config = AppConfigStore.Load();
        if (profileIndex >= config.Profiles.Count || config.Profiles.Count <= 1)
        {
            return;
        }

        ProfileConfig profile = config.Profiles[profileIndex];
        ContentDialog dialog = new()
        {
            Title = "Remove profile",
            Content = $"Remove \"{profile.Label}\" from the list? Its profile folder and login stay on disk.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            config.Profiles.RemoveAt(profileIndex);
            AppConfigStore.Save(config);
        }
    }

    private async Task StartProfileLoginAsync(int profileIndex)
    {
        AppConfig config = AppConfigStore.Load();
        if (profileIndex >= config.Profiles.Count)
        {
            return;
        }

        ProfileConfig profile = config.Profiles[profileIndex];
        string providerName = AppConfigStore.IsProvider(profile, "claude") ? "Claude" : "Codex";
        if (!ProfileLoginService.StartLogin(profile, out string errorMessage))
        {
            await ShowMessageAsync($"Could not start {providerName} login", errorMessage);
            return;
        }

        string message = AppConfigStore.IsProvider(profile, "claude")
            ? $"A console window should open to sign in profile \"{profile.Label}\". Refresh usage afterwards."
            : $"A browser window should open to sign in profile \"{profile.Label}\". Refresh usage afterwards.";
        await ShowMessageAsync($"{providerName} login started", message);
    }

    private async Task<string?> PromptForProfileNameAsync(string title, string initialText, string primaryButtonText)
    {
        TextBox nameBox = new()
        {
            Text = initialText,
            PlaceholderText = "Profile name",
            MinWidth = 280
        };

        ContentDialog dialog = new()
        {
            Title = title,
            Content = nameBox,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(initialText),
            XamlRoot = Root.XamlRoot
        };

        nameBox.TextChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text);
        };
        dialog.Opened += (_, _) =>
        {
            nameBox.Focus(FocusState.Programmatic);
            nameBox.SelectAll();
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? nameBox.Text : null;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Root.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OnWidgetGlowToggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingConfig)
        {
            return;
        }

        AppConfig config = AppConfigStore.Load();
        if (config.WidgetGlowEnabled != WidgetGlowToggle.IsOn)
        {
            config.WidgetGlowEnabled = WidgetGlowToggle.IsOn;
            AppConfigStore.Save(config);
        }
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingConfig)
        {
            return;
        }

        bool requested = StartupToggle.IsOn;
        if (WindowsStartupService.TrySetEnabled(requested, out string errorMessage))
        {
            return;
        }

        ApplyStartupState();
        await ShowMessageAsync("Could not update startup setting", errorMessage);
    }

    private void OnBackdropSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingConfig)
        {
            return;
        }

        AppConfig config = AppConfigStore.Load();
        string style = BackdropComboBox.SelectedIndex == 1 ? "solid" : "acrylic";
        if (!string.Equals(config.FlyoutStyle, style, StringComparison.OrdinalIgnoreCase))
        {
            config.FlyoutStyle = style;
            AppConfigStore.Save(config);
        }
    }

    private void OnRefreshIntervalValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isApplyingConfig)
        {
            return;
        }

        AppConfig config = AppConfigStore.Load();
        double rawValue = double.IsNaN(args.NewValue) ? config.RefreshIntervalSeconds : args.NewValue;
        int seconds = (int)Math.Clamp(Math.Round(rawValue), 30, 3600);

        if (double.IsNaN(sender.Value) || Math.Abs(sender.Value - seconds) > 0.1)
        {
            _isApplyingConfig = true;
            try
            {
                sender.Value = seconds;
            }
            finally
            {
                _isApplyingConfig = false;
            }
        }

        if (config.RefreshIntervalSeconds != seconds)
        {
            config.RefreshIntervalSeconds = seconds;
            AppConfigStore.Save(config);
        }
    }

    private void UpdateContentPanelWidth()
    {
        double viewportWidth = SettingsScroll.ActualWidth;
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            return;
        }

        double availableWidth = Math.Max(MinimumContentWidth, viewportWidth - (ContentSideMargin * 2));
        ContentPanel.Width = Math.Min(availableWidth, ContentPanel.MaxWidth);
    }

    public static void CloseInstance()
    {
        try
        {
            _instance?.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    public static void ShowInstance()
    {
        try
        {
            _instance ??= new SettingsWindow();
            _instance._appWindow.Show();
            _instance.Activate();
            NativeMethods.ForceForeground(_instance._hwnd);
        }
        catch (Exception ex)
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Fluent AgentBar",
                "winui-settings-error.log"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, ex.ToString());
            throw;
        }
    }

    private void ConfigureWindow()
    {
        SizeInt32 size = GetPhysicalWindowSize();
        _appWindow.Resize(size);

        DisplayArea displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workArea = displayArea.WorkArea;
        int x = workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2);
        int y = workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2);
        _appWindow.Move(new PointInt32(x, y));
    }

    private SizeInt32 GetPhysicalWindowSize()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        double scale = Math.Max(1.0, dpi / 96.0);
        return new SizeInt32(
            (int)Math.Round(WindowLogicalWidth * scale),
            (int)Math.Round(WindowLogicalHeight * scale)
        );
    }

    private static bool ShouldUseDarkTitleBar()
    {
        Windows.UI.Color background = new UISettings().GetColorValue(UIColorType.Background);
        return background.R < 128 && background.G < 128 && background.B < 128;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        AppConfigStore.Changed -= OnConfigChanged;
        WindowsStartupService.Changed -= OnStartupChanged;
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }
}
