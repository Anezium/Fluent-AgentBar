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
            return !string.IsNullOrWhiteSpace(GetRegisteredCommand());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read startup registration: {ex.Message}");
            return false;
        }
    }

    // Keep the Run key pointed at the exe that is actually running. Startup
    // can stay "on" across rebuilds while still launching an old artifact.
    internal static bool TrySyncRegisteredExecutable()
    {
        try
        {
            SyncRegisteredExecutable();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to sync startup registration: {ex.Message}");
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

    internal static bool NeedsStartupPathUpdate(string? registeredCommand, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(registeredCommand) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        string? registeredPath = TryParseExecutablePath(registeredCommand);
        if (string.IsNullOrWhiteSpace(registeredPath))
        {
            return true;
        }

        return !string.Equals(
            Path.GetFullPath(registeredPath),
            Path.GetFullPath(executablePath),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string? TryParseExecutablePath(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] == '"')
        {
            int end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }

        int space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    private static string? GetRegisteredCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    private static void SyncRegisteredExecutable()
    {
        string? registeredCommand = GetRegisteredCommand();
        if (string.IsNullOrWhiteSpace(registeredCommand))
        {
            return;
        }

        string executablePath = GetExecutablePath();
        if (!NeedsStartupPathUpdate(registeredCommand, executablePath))
        {
            return;
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows startup registry key is unavailable.");
        key.SetValue(ValueName, BuildStartupCommand(executablePath), RegistryValueKind.String);
        Changed?.Invoke(null, EventArgs.Empty);
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
