using System.Diagnostics;
using Microsoft.Win32;

namespace FluentAgentBar;

internal static class WindowsStartupService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "FluentAgentBar";

    internal static event EventHandler? Changed;

    internal static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string command &&
                !string.IsNullOrWhiteSpace(command);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read startup registration: {ex.Message}");
            return false;
        }
    }

    internal static bool TrySetEnabled(bool enabled, out string errorMessage)
    {
        try
        {
            SetEnabled(enabled);
            Changed?.Invoke(null, EventArgs.Empty);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to update startup registration: {ex.Message}");
            errorMessage = ex.Message;
            return false;
        }
    }

    internal static string BuildStartupCommand(string executablePath)
    {
        return $"\"{executablePath.Replace("\"", "\\\"")}\"";
    }

    private static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows startup registry key is unavailable.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ValueName, BuildStartupCommand(GetExecutablePath()), RegistryValueKind.String);
    }

    private static string GetExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return processPath;
        }

        string? mainModulePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(mainModulePath))
        {
            return mainModulePath;
        }

        throw new InvalidOperationException("The current executable path could not be resolved.");
    }
}
