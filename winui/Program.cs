using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluentAgentBar;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (InteractiveDesktopTestHook.IsEnabled)
        {
            InteractiveDesktopTestHook.AttachProcessToInteractiveWindowStation();
            Exception? uiFailure = null;
            Thread uiThread = new(() =>
            {
                try
                {
                    InteractiveDesktopTestHook.AttachCurrentThreadToDefaultDesktop();
                    RunWinUi();
                }
                catch (Exception ex)
                {
                    uiFailure = ex;
                }
            })
            {
                Name = "Fluent AgentBar WinUI STA",
                IsBackground = false
            };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            uiThread.Join();
            if (uiFailure is not null)
            {
                throw new InvalidOperationException("The interactive WinUI STA failed.", uiFailure);
            }

            return;
        }

        RunWinUi();
    }

    private static void RunWinUi()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(queue));
            new App();
        });
    }
}

internal static class InteractiveDesktopTestHook
{
    private const uint MaximumAllowed = 0x02000000;
    private static IntPtr _windowStation;
    private static IntPtr _desktop;

    // Test-only hook for CI/agent processes launched on a hidden desktop.
    // It is completely inert unless the explicit verification variable is 1.
    internal static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("FLUENTAGENTBAR_ATTACH_DESKTOP"),
            "1",
            StringComparison.Ordinal);

    internal static void AttachProcessToInteractiveWindowStation()
    {
        _windowStation = OpenWindowStation("WinSta0", false, MaximumAllowed);
        if (_windowStation == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "OpenWindowStation(WinSta0) failed.");
        }

        if (!SetProcessWindowStation(_windowStation))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "SetProcessWindowStation(WinSta0) failed.");
        }

        _desktop = OpenDesktop("Default", 0, false, MaximumAllowed);
        if (_desktop == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "OpenDesktop(Default) failed.");
        }

    }

    internal static void AttachCurrentThreadToDefaultDesktop()
    {
        if (!SetThreadDesktop(_desktop))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "SetThreadDesktop(Default) failed on the WinUI thread.");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenWindowStation(
        string windowStationName,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWindowStation(IntPtr windowStation);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenDesktop(
        string desktopName,
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);
}
