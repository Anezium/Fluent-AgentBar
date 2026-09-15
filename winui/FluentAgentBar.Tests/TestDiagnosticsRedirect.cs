using System.Runtime.CompilerServices;

namespace FluentAgentBar.Tests;

// Default-redirect provider diagnostics for every test run so a forgotten
// fixture cannot append to the real %APPDATA%\Fluent AgentBar\log.
internal static class TestDiagnosticsRedirect
{
    internal static string Directory { get; } = Path.Combine(
        Path.GetTempPath(),
        "FluentAgentBar.Tests",
        "provider-diagnostics");

    [ModuleInitializer]
    internal static void RedirectProviderDiagnosticsLog()
    {
        System.IO.Directory.CreateDirectory(Directory);
        ProviderDiagnostics.LogDirectoryOverride = Directory;
    }
}
