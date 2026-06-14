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
}
