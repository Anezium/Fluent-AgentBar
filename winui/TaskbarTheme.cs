using Microsoft.Win32;

namespace FluentAgentBar;

internal static class TaskbarTheme
{
    public static bool IsDark()
    {
        using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize"
        );

        object? systemUsesLightTheme = personalize?.GetValue("SystemUsesLightTheme");
        return systemUsesLightTheme is int value ? value == 0 : true;
    }
}
