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

    [Fact]
    public void CreateGeminiLoginStartInfo_RunsTheCliInAVisibleConsole()
    {
        string profileHome = Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests", "gemini-login");

        System.Diagnostics.ProcessStartInfo startInfo =
            ProfileLoginService.CreateGeminiLoginStartInfo(profileHome);

        Assert.False(startInfo.CreateNoWindow);
        Assert.Contains("/K", startInfo.ArgumentList);
        Assert.Contains("gemini", startInfo.ArgumentList);
        Assert.Equal(profileHome, startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateGrokLoginStartInfo_RunsGrokLoginInAVisibleConsole()
    {
        string profileHome = Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests", "grok-login");

        System.Diagnostics.ProcessStartInfo startInfo =
            ProfileLoginService.CreateGrokLoginStartInfo(profileHome);

        Assert.False(startInfo.CreateNoWindow);
        Assert.Contains("/K", startInfo.ArgumentList);
        Assert.Contains("grok login", startInfo.ArgumentList);
        Assert.Equal(profileHome, startInfo.WorkingDirectory);
    }

    [Fact]
    public void ResolveOnPath_FindsACommandThroughPathAndPathExt()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "FluentAgentBar.Tests",
            "path-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "fake-agent-cli.cmd");
        File.WriteAllText(executable, "@echo off");

        try
        {
            Assert.Equal(
                executable,
                ProfileLoginService.ResolveOnPath("fake-agent-cli", directory, ".EXE;.CMD"),
                StringComparer.OrdinalIgnoreCase);
            Assert.Null(ProfileLoginService.ResolveOnPath("fake-agent-cli", directory, ".EXE"));
            Assert.Null(ProfileLoginService.ResolveOnPath("missing-agent-cli", directory, ".EXE;.CMD"));
            Assert.Null(ProfileLoginService.ResolveOnPath("fake-agent-cli", null, null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
