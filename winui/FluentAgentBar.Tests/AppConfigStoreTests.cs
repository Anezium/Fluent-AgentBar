using Xunit;

namespace FluentAgentBar.Tests;

public sealed class AppConfigStoreTests
{
    [Theory]
    [InlineData("claude", "claude")]
    [InlineData(" Claude ", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("unknown", "codex")]
    [InlineData("", "codex")]
    [InlineData(null, "codex")]
    public void NormalizeProvider_DefaultsUnknownBlankAndNullToCodex(string? provider, string expected)
    {
        Assert.Equal(expected, AppConfigStore.NormalizeProvider(provider));
    }

    [Fact]
    public void ProfilePathLabel_ShortensAppDataPathWithoutLosingSuffix()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string path = Path.Combine(appData, "Fluent AgentBar", "profiles", "main");

        string label = AppConfigStore.ProfilePathLabel(path);

        Assert.StartsWith("%APPDATA%", label, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Fluent AgentBar", "profiles", "main"), label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfilePathLabel_ShortensUserProfilePathWithoutLosingSuffix()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Path.Combine(userProfile, ".claude", "projects");

        string label = AppConfigStore.ProfilePathLabel(path);

        Assert.StartsWith("%USERPROFILE%", label, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine(".claude", "projects"), label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadWithStatus_InvalidJsonReturnsFallbackWithoutReplacingConfig()
    {
        using TempConfigRoot root = TempConfigRoot.Create();
        string invalidJson = "{ this is not valid json";
        File.WriteAllText(root.Paths.ConfigPath, invalidJson);

        AppConfigLoadResult result = AppConfigStore.LoadWithStatus(root.Paths);

        Assert.Equal(AppConfigLoadStatus.ErrorFallback, result.Status);
        Assert.True(result.UsedFallback);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.NotEmpty(result.Config.Profiles);
        Assert.Equal(invalidJson, File.ReadAllText(root.Paths.ConfigPath));
    }

    [Fact]
    public void LoadWithStatus_MissingConfigReturnsFirstRunDefaultWithoutWriting()
    {
        using TempConfigRoot root = TempConfigRoot.Create();

        AppConfigLoadResult result = AppConfigStore.LoadWithStatus(root.Paths);

        Assert.Equal(AppConfigLoadStatus.FirstRunDefault, result.Status);
        Assert.False(result.UsedFallback);
        Assert.Null(result.ErrorMessage);
        Assert.NotEmpty(result.Config.Profiles);
        Assert.False(File.Exists(root.Paths.ConfigPath));
    }

    [Fact]
    public void EnsureConfigFileExists_DoesNotReplaceExistingInvalidConfig()
    {
        using TempConfigRoot root = TempConfigRoot.Create();
        string invalidJson = "{ broken";
        File.WriteAllText(root.Paths.ConfigPath, invalidJson);

        bool created = AppConfigStore.EnsureConfigFileExists(root.Paths);

        Assert.False(created);
        Assert.Equal(invalidJson, File.ReadAllText(root.Paths.ConfigPath));
        Assert.Empty(Directory.GetFiles(root.Paths.ConfigDirectory, "config.invalid-*.json"));
    }

    [Fact]
    public void EnsureConfigFileExists_CreatesDefaultWhenMissing()
    {
        using TempConfigRoot root = TempConfigRoot.Create();

        bool created = AppConfigStore.EnsureConfigFileExists(root.Paths);

        Assert.True(created);
        Assert.True(File.Exists(root.Paths.ConfigPath));

        AppConfigLoadResult result = AppConfigStore.LoadWithStatus(root.Paths);
        Assert.Equal(AppConfigLoadStatus.ExistingConfig, result.Status);
        Assert.Contains(result.Config.Profiles, profile =>
            AppConfigStore.IsProvider(profile, "codex") &&
            profile.Home.StartsWith(root.Paths.ConfigDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Save_BackupsInvalidConfigBeforeOverwriting()
    {
        using TempConfigRoot root = TempConfigRoot.Create();
        string invalidJson = "{ broken";
        File.WriteAllText(root.Paths.ConfigPath, invalidJson);

        AppConfigStore.Save(new AppConfig { Profiles = [] }, root.Paths);

        string backupPath = Assert.Single(Directory.GetFiles(root.Paths.ConfigDirectory, "config.invalid-*.json"));
        Assert.Equal(invalidJson, File.ReadAllText(backupPath));

        AppConfigLoadResult result = AppConfigStore.LoadWithStatus(root.Paths);
        Assert.Equal(AppConfigLoadStatus.ExistingConfig, result.Status);
        Assert.NotEmpty(result.Config.Profiles);
    }

    private sealed class TempConfigRoot : IDisposable
    {
        private TempConfigRoot(string root)
        {
            Root = root;
            string configDirectory = Path.Combine(root, "config");
            string legacyConfigDirectory = Path.Combine(root, "legacy");
            Directory.CreateDirectory(configDirectory);
            Directory.CreateDirectory(legacyConfigDirectory);
            Paths = new AppConfigStorePaths(configDirectory, legacyConfigDirectory);
        }

        public string Root { get; }

        public AppConfigStorePaths Paths { get; }

        public static TempConfigRoot Create()
        {
            return new TempConfigRoot(Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests", Guid.NewGuid().ToString("N")));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
