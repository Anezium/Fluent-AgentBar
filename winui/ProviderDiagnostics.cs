using System.Diagnostics;
using System.Globalization;

namespace FluentAgentBar;

// Append-only log of provider fetch failures next to config.json, so a
// "Login Required" or "unavailable" card can be diagnosed after the fact.
// Messages must never contain tokens or emails; callers pass exception
// types/messages only.
internal static class ProviderDiagnostics
{
    private const long MaxLogBytes = 512 * 1024;
    private static readonly object Sync = new();

    // Tests redirect this so Record() never touches the real %APPDATA% log.
    internal static string? LogDirectoryOverride { get; set; }

    internal static string LogDirectory => LogDirectoryOverride ?? AppConfigStore.ConfigDirectory;

    internal static string LogPath => Path.Combine(LogDirectory, "provider-errors.log");

    internal static IDisposable UseLogDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return new LogDirectoryScope(directory);
    }

    internal static void Record(string provider, string profileLabel, Exception exception)
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"[{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}] {provider}/{profileLabel}: {Describe(exception)}");
        Debug.WriteLine(line);

        try
        {
            lock (Sync)
            {
                string logPath = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                if (File.Exists(logPath) && new FileInfo(logPath).Length > MaxLogBytes)
                {
                    File.Delete(logPath);
                }

                File.AppendAllText(logPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never take the refresh down with them.
        }
    }

    private static string Describe(Exception exception)
    {
        List<string> parts = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" <- ", parts);
    }

    private sealed class LogDirectoryScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public LogDirectoryScope(string directory)
        {
            lock (Sync)
            {
                _previous = LogDirectoryOverride;
                LogDirectoryOverride = directory;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (Sync)
            {
                LogDirectoryOverride = _previous;
            }

            _disposed = true;
        }
    }
}
