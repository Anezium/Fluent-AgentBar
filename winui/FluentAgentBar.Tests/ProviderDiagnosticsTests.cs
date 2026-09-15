using Xunit;

namespace FluentAgentBar.Tests;

[Collection("ProviderDiagnostics")]
public sealed class ProviderDiagnosticsTests
{
    [Fact]
    public void LogPath_WithoutOverride_UsesAppDataConfigDirectory()
    {
        string? previous = ProviderDiagnostics.LogDirectoryOverride;
        try
        {
            ProviderDiagnostics.LogDirectoryOverride = null;
            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Fluent AgentBar",
                "provider-errors.log");

            Assert.Equal(expected, ProviderDiagnostics.LogPath);
        }
        finally
        {
            ProviderDiagnostics.LogDirectoryOverride = previous;
        }
    }

    [Fact]
    public void Record_WritesToOverrideDirectoryAndLeavesAppDataLogUntouched()
    {
        string appDataLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Fluent AgentBar",
            "provider-errors.log");
        string marker = $"isolation-{Guid.NewGuid():N}";

        using TempLogDirectory temp = TempLogDirectory.Create();
        using (ProviderDiagnostics.UseLogDirectory(temp.Directory))
        {
            ProviderDiagnostics.Record(
                "claude",
                "Broken",
                new InvalidOperationException(marker));

            Assert.True(File.Exists(ProviderDiagnostics.LogPath));
            Assert.Contains(marker, File.ReadAllText(ProviderDiagnostics.LogPath), StringComparison.Ordinal);
            Assert.False(
                ProviderDiagnostics.LogPath.StartsWith(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    StringComparison.OrdinalIgnoreCase));
        }

        if (File.Exists(appDataLog))
        {
            Assert.DoesNotContain(marker, File.ReadAllText(appDataLog));
        }
    }

    private sealed class TempLogDirectory : IDisposable
    {
        private TempLogDirectory(string directory)
        {
            Directory = directory;
            System.IO.Directory.CreateDirectory(directory);
        }

        public string Directory { get; }

        public static TempLogDirectory Create()
        {
            return new TempLogDirectory(
                Path.Combine(Path.GetTempPath(), "FluentAgentBar.Tests", Guid.NewGuid().ToString("N")));
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
