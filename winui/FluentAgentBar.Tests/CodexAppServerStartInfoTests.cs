using Xunit;

namespace FluentAgentBar.Tests;

public sealed class CodexAppServerStartInfoTests
{
    [Fact]
    public void CreateCodexAppServerStartInfo_UsesSupportedNonInteractiveApprovalPolicy()
    {
        const string codexHome = @"C:\agentbar-test-codex-home";

        System.Diagnostics.ProcessStartInfo startInfo =
            UsageService.CreateCodexAppServerStartInfo(codexHome);

        Assert.Equal(
            ["/D", "/S", "/C", "codex -s read-only -a never app-server"],
            startInfo.ArgumentList);
        Assert.Equal(codexHome, startInfo.Environment["CODEX_HOME"]);
    }
}
