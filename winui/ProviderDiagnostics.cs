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

    internal static string LogPath => Path.Combine(AppConfigStore.ConfigDirectory, "provider-errors.log");

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
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                {
                    File.Delete(LogPath);
                }

                File.AppendAllText(LogPath, line + Environment.NewLine);
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
}
