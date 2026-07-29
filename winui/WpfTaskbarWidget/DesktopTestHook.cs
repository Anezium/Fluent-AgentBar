using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluentAgentBar.WpfTaskbarWidget;

internal static class DesktopTestHook
{
    // Test-only escape hatch for automation launched on a non-interactive
    // desktop. Normal launches never open or switch desktops.
    internal static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("FLUENTAGENTBAR_ATTACH_DESKTOP"),
            "1",
            StringComparison.Ordinal);

    internal static IntPtr AttachCurrentThreadIfRequested()
    {
        if (!IsEnabled)
        {
            return IntPtr.Zero;
        }

        IntPtr desktop = NativeMethods.OpenDesktop(
            "Default",
            0,
            false,
            NativeMethods.MaximumAllowed);
        if (desktop == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "OpenDesktop(Default) failed on the WPF widget STA.");
        }

        if (!NativeMethods.SetThreadDesktop(desktop))
        {
            int error = Marshal.GetLastWin32Error();
            NativeMethods.CloseDesktop(desktop);
            throw new Win32Exception(
                error,
                "SetThreadDesktop(Default) failed on the WPF widget STA.");
        }

        return desktop;
    }
}
