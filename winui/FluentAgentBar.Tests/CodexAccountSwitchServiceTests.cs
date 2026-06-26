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
