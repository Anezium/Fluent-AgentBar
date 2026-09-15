using Xunit;

namespace FluentAgentBar.Tests;

public sealed class WindowsStartupServiceTests
{
    [Fact]
    public void BuildStartupCommand_QuotesExecutablePath()
    {
        string command = WindowsStartupService.BuildStartupCommand(
            @"C:\Program Files\Fluent AgentBar\FluentAgentBar.exe");

        Assert.Equal(@"""C:\Program Files\Fluent AgentBar\FluentAgentBar.exe""", command);
    }

    [Fact]
    public void TryParseExecutablePath_ReadsQuotedPath()
    {
        string? path = WindowsStartupService.TryParseExecutablePath(
            @"""E:\Tools\Fluent-AgentBar\artifacts\FluentAgentBar-v0.3.1-win-x64\FluentAgentBar.exe""");

        Assert.Equal(
            @"E:\Tools\Fluent-AgentBar\artifacts\FluentAgentBar-v0.3.1-win-x64\FluentAgentBar.exe",
            path);
    }

    [Fact]
    public void NeedsStartupPathUpdate_IgnoresMatchingQuotedPath()
    {
        const string exe = @"E:\Tools\Fluent-AgentBar\winui\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\FluentAgentBar.exe";

        Assert.False(WindowsStartupService.NeedsStartupPathUpdate(
            WindowsStartupService.BuildStartupCommand(exe),
            exe));
    }

    [Fact]
    public void NeedsStartupPathUpdate_DetectsStaleArtifactPath()
    {
        const string registered = @"""E:\Tools\Fluent-AgentBar\artifacts\FluentAgentBar-v0.3.1-win-x64\FluentAgentBar.exe""";
        const string current = @"E:\Tools\Fluent-AgentBar\winui\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\FluentAgentBar.exe";

        Assert.True(WindowsStartupService.NeedsStartupPathUpdate(registered, current));
    }

    [Fact]
    public void NeedsStartupPathUpdate_DoesNothingWhenStartupIsDisabled()
    {
        Assert.False(WindowsStartupService.NeedsStartupPathUpdate(
            null,
            @"E:\Tools\Fluent-AgentBar\FluentAgentBar.exe"));
    }
}
