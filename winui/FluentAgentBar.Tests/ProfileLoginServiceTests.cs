using Xunit;

namespace FluentAgentBar.Tests;

public sealed class ProfileLoginServiceTests
{
    [Fact]
    public void CreateClaudeLoginStartInfo_ClearsInferenceOnlyEnvironmentToken()
    {
        string profileHome = Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests", "claude-login");

        System.Diagnostics.ProcessStartInfo startInfo =
            ProfileLoginService.CreateClaudeLoginStartInfo(profileHome);

        Assert.Equal(string.Empty, startInfo.Environment["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Equal(string.Empty, startInfo.Environment["CLAUDE_CODE_OAUTH_SCOPES"]);
        Assert.Equal(profileHome, startInfo.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(profileHome, startInfo.WorkingDirectory);
        Assert.Contains("claude /login", startInfo.ArgumentList);
    }
}
