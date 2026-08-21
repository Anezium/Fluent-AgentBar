using Xunit;

namespace FluentAgentBar.Tests;

public sealed class CodexAccountSwitchServiceTests
{
    [Fact]
    public void CopyProfileAuthToCodexHome_WritesAuthJson()
    {
        using TempCodexHomes homes = TempCodexHomes.Create();
        ProfileConfig profile = homes.CreateProfile("Work");
        string sourceAuth = CodexAccountSwitchService.ProfileAuthPath(profile);
        File.WriteAllText(sourceAuth, """{"tokens":{"account_id":"acct_work","refresh_token":"refresh_work"}}""");

        CodexAccountSwitchService.CopyProfileAuthToCodexHome(profile, homes.DefaultCodexHome);

        string targetAuth = Path.Combine(homes.DefaultCodexHome, "auth.json");
        Assert.True(File.Exists(targetAuth));
        Assert.Equal(File.ReadAllText(sourceAuth), File.ReadAllText(targetAuth));
    }

    [Fact]
    public void IsActiveProfile_ComparesTokenIdentityInsteadOfRefreshTimestamp()
    {
        using TempCodexHomes homes = TempCodexHomes.Create();
        ProfileConfig profile = homes.CreateProfile("Work");
        File.WriteAllText(
            CodexAccountSwitchService.ProfileAuthPath(profile),
            """{"tokens":{"account_id":"acct_work","refresh_token":"refresh_work"},"last_refresh":"2026-01-01T00:00:00Z"}""");
        File.WriteAllText(
            Path.Combine(homes.DefaultCodexHome, "auth.json"),
            """{"tokens":{"account_id":"acct_work","refresh_token":"newer_refresh"},"last_refresh":"2026-02-01T00:00:00Z"}""");

        Assert.True(CodexAccountSwitchService.IsActiveProfile(profile, homes.DefaultCodexHome));
    }

    [Fact]
    public void IsActiveProfile_ReturnsFalseForDifferentAccountIdentity()
    {
        using TempCodexHomes homes = TempCodexHomes.Create();
        ProfileConfig profile = homes.CreateProfile("Work");
        File.WriteAllText(
            CodexAccountSwitchService.ProfileAuthPath(profile),
            """{"tokens":{"account_id":"acct_work","refresh_token":"refresh_work"}}""");
        File.WriteAllText(
            Path.Combine(homes.DefaultCodexHome, "auth.json"),
            """{"tokens":{"account_id":"acct_personal","refresh_token":"refresh_personal"}}""");

        Assert.False(CodexAccountSwitchService.IsActiveProfile(profile, homes.DefaultCodexHome));
    }

    [Fact]
    public void TrySynchronizeProfileAuthFromCodexHome_WhenActiveCopyIsNewer_UpdatesMatchingProfile()
    {
        using TempCodexHomes homes = TempCodexHomes.Create();
        ProfileConfig profile = homes.CreateProfile("Work");
        string profileAuthPath = CodexAccountSwitchService.ProfileAuthPath(profile);
        string activeAuthPath = Path.Combine(homes.DefaultCodexHome, "auth.json");
        File.WriteAllText(
            profileAuthPath,
            """{"tokens":{"account_id":"acct_work","refresh_token":"stale_refresh"}}""");
        File.WriteAllText(
            activeAuthPath,
            """{"tokens":{"account_id":"acct_work","refresh_token":"fresh_refresh"}}""");
        File.SetLastWriteTimeUtc(profileAuthPath, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(activeAuthPath, DateTime.UtcNow);

        bool synchronized = CodexAccountSwitchService.TrySynchronizeProfileAuthFromCodexHome(
            profile,
            homes.DefaultCodexHome);

        Assert.True(synchronized);
        Assert.Equal(File.ReadAllText(activeAuthPath), File.ReadAllText(profileAuthPath));
    }

    [Fact]
    public void TrySynchronizeProfileAuthFromCodexHome_WhenAccountsDiffer_LeavesProfileUntouched()
    {
        using TempCodexHomes homes = TempCodexHomes.Create();
        ProfileConfig profile = homes.CreateProfile("Work");
        string profileAuthPath = CodexAccountSwitchService.ProfileAuthPath(profile);
        string activeAuthPath = Path.Combine(homes.DefaultCodexHome, "auth.json");
        const string originalProfileAuth =
            """{"tokens":{"account_id":"acct_work","refresh_token":"work_refresh"}}""";
        File.WriteAllText(profileAuthPath, originalProfileAuth);
        File.WriteAllText(
            activeAuthPath,
            """{"tokens":{"account_id":"acct_personal","refresh_token":"personal_refresh"}}""");
        File.SetLastWriteTimeUtc(profileAuthPath, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(activeAuthPath, DateTime.UtcNow);

        bool synchronized = CodexAccountSwitchService.TrySynchronizeProfileAuthFromCodexHome(
            profile,
            homes.DefaultCodexHome);

        Assert.False(synchronized);
        Assert.Equal(originalProfileAuth, File.ReadAllText(profileAuthPath));
    }

    private sealed class TempCodexHomes : IDisposable
    {
        private TempCodexHomes(string root)
        {
            Root = root;
            DefaultCodexHome = Path.Combine(root, ".codex");
            Directory.CreateDirectory(DefaultCodexHome);
        }

        private string Root { get; }

        public string DefaultCodexHome { get; }

        public static TempCodexHomes Create()
        {
            return new TempCodexHomes(Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests", Guid.NewGuid().ToString("N")));
        }

        public ProfileConfig CreateProfile(string label)
        {
            string home = Path.Combine(Root, "profiles", label.ToLowerInvariant());
            Directory.CreateDirectory(home);
            return new ProfileConfig
            {
                Provider = "codex",
                Label = label,
                Home = home,
                Enabled = true
            };
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
