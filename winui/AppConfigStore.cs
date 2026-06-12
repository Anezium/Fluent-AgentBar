using System.Text.Json;
namespace FluentAgentBar;

internal sealed class AppConfig
{
    public int RefreshIntervalSeconds { get; set; } = 300;
    public string FlyoutStyle { get; set; } = "acrylic";
    public bool WidgetGlowEnabled { get; set; } = true;
    public List<ProfileConfig> Profiles { get; set; } = [];
}

internal sealed class ProfileConfig
{
    public string Provider { get; set; } = "codex";
    public string Label { get; set; } = "Main";
    public string Home { get; set; } = AppConfigStore.DefaultCodexProfileHome;
    public bool Enabled { get; set; } = true;
}

internal static class AppConfigStore
{
    private const string ConfigFolderName = "Fluent AgentBar";
    private const string LegacyConfigFolderName = "Codex SWBar Windows";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static event EventHandler? Changed;

    internal static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ConfigFolderName
    );

    internal static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    internal static string DefaultCodexProfileHome => Path.Combine(ConfigDirectory, "profiles", "main");

    internal static string DefaultClaudeProfileHome => "%USERPROFILE%\\.claude";

    private static string LegacyConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        LegacyConfigFolderName
    );

    private static string LegacyConfigPath => Path.Combine(LegacyConfigDirectory, "config.json");

    internal static string CodexProfileHomeForLabel(string label)
    {
        return DefaultHomeForLabel("codex", label);
    }

    internal static string DefaultHomeForLabel(string provider, string label)
    {
        string normalizedProvider = NormalizeProvider(provider);
        bool firstClaude = string.Equals(normalizedProvider, "claude", StringComparison.Ordinal) &&
            !ConfigContainsClaudeProfile();
        return DefaultHomeForLabel(normalizedProvider, label, firstClaude);
    }

    internal static string NormalizeProvider(string? provider)
    {
        return string.Equals(provider?.Trim(), "claude", StringComparison.OrdinalIgnoreCase)
            ? "claude"
            : "codex";
    }

    internal static bool IsProvider(ProfileConfig profile, string provider)
    {
        return string.Equals(
            NormalizeProvider(profile.Provider),
            NormalizeProvider(provider),
            StringComparison.Ordinal);
    }

    private static string DefaultHomeForLabel(string provider, string label, bool firstClaude)
    {
        string normalizedProvider = NormalizeProvider(provider);
        if (string.Equals(normalizedProvider, "claude", StringComparison.Ordinal) && firstClaude)
        {
            return DefaultClaudeProfileHome;
        }

        string slug = SlugForLabel(label);
        string folder = string.Equals(normalizedProvider, "claude", StringComparison.Ordinal)
            ? "claude-" + slug
            : slug;
        return Path.Combine(ConfigDirectory, "profiles", folder);
    }

    internal static AppConfig Load()
    {
        CopyLegacyConfigIfNeeded();

        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                AppConfig? config = DeserializeConfig(json);
                if (config is not null)
                {
                    return Normalize(config);
                }
            }
        }
        catch
        {
        }

        return Normalize(CreateDefaultConfig());
    }

    private static void CopyLegacyConfigIfNeeded()
    {
        try
        {
            if (File.Exists(ConfigPath) || !File.Exists(LegacyConfigPath))
            {
                return;
            }

            Directory.CreateDirectory(ConfigDirectory);
            File.Copy(LegacyConfigPath, ConfigPath);
        }
        catch
        {
        }
    }

    internal static void Save(AppConfig config)
    {
        AppConfig normalized = Normalize(config);
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(normalized, JsonOptions));
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static string ProfilePathLabel(string path)
    {
        if (path.StartsWith("%APPDATA%", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string expandedPath = Environment.ExpandEnvironmentVariables(path);
        if (!string.IsNullOrWhiteSpace(appData) &&
            expandedPath.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
        {
            return "%APPDATA%" + expandedPath[appData.Length..];
        }

        if (!string.IsNullOrWhiteSpace(userProfile) &&
            expandedPath.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "%USERPROFILE%" + expandedPath[userProfile.Length..];
        }

        return path;
    }

    private static AppConfig? DeserializeConfig(string json)
    {
        AppConfigDto? dto = JsonSerializer.Deserialize<AppConfigDto>(json, JsonOptions);
        if (dto is null)
        {
            return null;
        }

        AppConfig config = new()
        {
            RefreshIntervalSeconds = dto.RefreshIntervalSeconds ?? 300,
            FlyoutStyle = string.IsNullOrWhiteSpace(dto.FlyoutStyle) ? "acrylic" : dto.FlyoutStyle,
            WidgetGlowEnabled = dto.WidgetGlowEnabled ?? true,
            Profiles = dto.Profiles is null ? [] : [.. dto.Profiles]
        };

        if (config.Profiles.Count == 0 && dto.CodexProfiles is not null)
        {
            foreach (LegacyCodexProfileConfig legacyProfile in dto.CodexProfiles)
            {
                config.Profiles.Add(new ProfileConfig
                {
                    Provider = "codex",
                    Label = legacyProfile.Label,
                    Home = legacyProfile.CodexHome,
                    Enabled = legacyProfile.Enabled
                });
            }
        }

        if (dto.Claude?.Enabled == true &&
            !config.Profiles.Any(profile => IsProvider(profile, "claude")))
        {
            config.Profiles.Add(new ProfileConfig
            {
                Provider = "claude",
                Label = "Personal",
                Home = DefaultClaudeProfileHome,
                Enabled = true
            });
        }

        return config;
    }

    private static AppConfig CreateDefaultConfig()
    {
        return new AppConfig
        {
            Profiles =
            [
                new ProfileConfig
                {
                    Provider = "codex",
                    Label = "Main",
                    Home = DefaultHomeForLabel("codex", "Main", firstClaude: false),
                    Enabled = true
                },
                new ProfileConfig
                {
                    Provider = "claude",
                    Label = "Personal",
                    Home = DefaultClaudeProfileHome,
                    Enabled = true
                }
            ]
        };
    }

    private static AppConfig Normalize(AppConfig config)
    {
        config.RefreshIntervalSeconds = Math.Clamp(config.RefreshIntervalSeconds, 30, 3600);
        config.FlyoutStyle = string.Equals(config.FlyoutStyle, "solid", StringComparison.OrdinalIgnoreCase)
            ? "solid"
            : "acrylic";
        config.Profiles ??= [];

        if (config.Profiles.Count == 0)
        {
            config.Profiles.Add(new ProfileConfig
            {
                Provider = "codex",
                Label = "Main",
                Home = DefaultHomeForLabel("codex", "Main", firstClaude: false),
                Enabled = true
            });
        }

        int claudeProfilesSeen = 0;
        foreach (ProfileConfig profile in config.Profiles)
        {
            profile.Provider = NormalizeProvider(profile.Provider);

            if (string.IsNullOrWhiteSpace(profile.Label))
            {
                profile.Label = IsProvider(profile, "claude") ? "Personal" : "Main";
            }

            if (IsProvider(profile, "claude"))
            {
                bool firstClaude = claudeProfilesSeen == 0;
                claudeProfilesSeen++;
                if (string.IsNullOrWhiteSpace(profile.Home))
                {
                    profile.Home = DefaultHomeForLabel(profile.Provider, profile.Label, firstClaude);
                }
            }
            else if (string.IsNullOrWhiteSpace(profile.Home))
            {
                profile.Home = DefaultHomeForLabel(profile.Provider, profile.Label, firstClaude: false);
            }
        }

        return config;
    }

    private static bool ConfigContainsClaudeProfile()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return false;
            }

            string json = File.ReadAllText(ConfigPath);
            AppConfigDto? dto = JsonSerializer.Deserialize<AppConfigDto>(json, JsonOptions);
            if (dto is null)
            {
                return false;
            }

            if (dto.Profiles?.Any(profile => IsProvider(profile, "claude")) == true)
            {
                return true;
            }

            return dto.Claude?.Enabled == true;
        }
        catch
        {
            return false;
        }
    }

    private static string SlugForLabel(string label)
    {
        char[] slug = label.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        string folder = new string(slug).Trim('-');
        return folder.Length == 0 ? "profile" : folder;
    }

    private sealed class AppConfigDto
    {
        public int? RefreshIntervalSeconds { get; set; }
        public string? FlyoutStyle { get; set; }
        public bool? WidgetGlowEnabled { get; set; }
        public List<ProfileConfig>? Profiles { get; set; }
        public List<LegacyCodexProfileConfig>? CodexProfiles { get; set; }
        public LegacyClaudeConfig? Claude { get; set; }
    }

    private sealed class LegacyCodexProfileConfig
    {
        public string Label { get; set; } = "Main";
        public string CodexHome { get; set; } = DefaultCodexProfileHome;
        public bool Enabled { get; set; } = true;
    }

    private sealed class LegacyClaudeConfig
    {
        public bool Enabled { get; set; }
    }
}
