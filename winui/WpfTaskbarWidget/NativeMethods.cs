using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FluentAgentBar.WpfTaskbarWidget;

internal static class NativeMethods
{
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const int GwlpHwndParent = -8;
    internal const long WsChild = 0x40000000L;
    internal const long WsPopup = unchecked((long)0x80000000);
    internal const long WsVisible = 0x10000000L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExAppWindow = 0x00040000L;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpShowWindow = 0x0040;
    internal const int SwHide = 0;
    internal const int SwShowNoActivate = 4;
    internal const int WmDisplayChange = 0x007E;
    internal const int WmDpiChanged = 0x02E0;
    internal const int WmSettingChange = 0x001A;
    internal const int WmNcDestroy = 0x0082;
    internal const int WmMouseActivate = 0x0021;
    internal const int WmLeftButtonUp = 0x0202;
    internal const int WmRightButtonUp = 0x0205;
    internal const int MaNoActivate = 3;
    internal const int DebugExitMessage = 0x8000 + 0x34A;
    internal const int DebugDetachMessage = 0x8000 + 0x34B;
    internal const string ModePropertyName = "FluentAgentBar.WpfWidget.Mode";
    internal const uint MaximumAllowed = 0x02000000;
    private const int MonitorDefaultToNearest = 2;
    private const int FullscreenTolerance = 4;
    internal static readonly IntPtr HwndTopMost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly NativeRect ToNativeRect() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;

        internal Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowEx(
        IntPtr parent,
        IntPtr childAfter,
        string className,
        string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maximumCount);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(
        IntPtr hwnd,
        IntPtr region,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint errorCode);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr hwnd, string name, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RemoveProp(IntPtr hwnd, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenDesktop(
        string desktopName,
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(IntPtr desktop);

    internal static long GetStyle(IntPtr hwnd, int index) =>
        GetWindowLongPtr(hwnd, index).ToInt64();

    internal static void SetStyle(IntPtr hwnd, int index, long value, string operation)
    {
        SetLastError(0);
        IntPtr result = SetWindowLongPtr(hwnd, index, new IntPtr(value));
        int error = Marshal.GetLastWin32Error();
        if (result == IntPtr.Zero && error != 0)
        {
            throw new Win32Exception(error, operation);
        }
    }

    internal static void SetOwner(IntPtr hwnd, IntPtr owner) =>
        SetStyle(hwnd, GwlpHwndParent, owner.ToInt64(), "SetWindowLongPtr(GWLP_HWNDPARENT) failed.");

    internal static void ApplyRoundedRegion(IntPtr hwnd, NativeRect rect, uint dpi)
    {
        double scale = Math.Max(1.0, (dpi == 0 ? 96u : dpi) / 96d);
        int radius = Math.Max(8, (int)Math.Round(TaskbarWidgetLayoutCalculator.CornerRadiusLogical * scale));
        IntPtr region = CreateRoundRectRgn(
            rect.Left,
            rect.Top,
            rect.Right + 1,
            rect.Bottom + 1,
            radius,
            radius);
        if (region == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRoundRectRgn failed.");
        }

        // On success USER owns the HRGN. GetLastError may remain stale, so it
        // is intentionally read only on the failure path.
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            DeleteObject(region);
            throw new Win32Exception(error, "SetWindowRgn failed.");
        }
    }

    internal static NativeRect GetRequiredRect(IntPtr hwnd, string name)
    {
        if (!GetWindowRect(hwnd, out Rect rect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetWindowRect failed for {name}.");
        }

        return rect.ToNativeRect();
    }

    internal static bool IsForegroundWindowFullscreenOnMonitor(IntPtr referenceHwnd)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero ||
            foreground == referenceHwnd ||
            IsShellWindow(foreground) ||
            !IsWindowVisible(foreground) ||
            !GetWindowRect(foreground, out Rect foregroundRect))
        {
            return false;
        }

        IntPtr monitor = MonitorFromWindow(referenceHwnd, MonitorDefaultToNearest);
        MonitorInfo monitorInfo = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        Rect monitorRect = monitorInfo.Monitor;
        return
            foregroundRect.Left <= monitorRect.Left + FullscreenTolerance &&
            foregroundRect.Top <= monitorRect.Top + FullscreenTolerance &&
            foregroundRect.Right >= monitorRect.Right - FullscreenTolerance &&
            foregroundRect.Bottom >= monitorRect.Bottom - FullscreenTolerance;
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        StringBuilder className = new(64);
        if (GetClassName(hwnd, className, className.Capacity) == 0)
        {
            return false;
        }

        string value = className.ToString();
        return
            value == "Progman" ||
            value == "WorkerW" ||
            value == "Shell_TrayWnd" ||
            value == "Shell_SecondaryTrayWnd";
    }

    internal static bool HasExpectedTaskbarClass(IntPtr hwnd, bool isPrimary)
    {
        if (!IsWindow(hwnd))
        {
            return false;
        }

        StringBuilder className = new(64);
        if (GetClassName(hwnd, className, className.Capacity) == 0)
        {
            return false;
        }

        string expected = isPrimary ? "Shell_TrayWnd" : "Shell_SecondaryTrayWnd";
        return string.Equals(className.ToString(), expected, StringComparison.Ordinal);
    }

    internal static void SetModeProperty(IntPtr hwnd, TaskbarWidgetMode mode)
    {
        _ = SetProp(hwnd, ModePropertyName, new IntPtr(mode == TaskbarWidgetMode.Child ? 1 : 2));
    }

    internal static void ClearModeProperty(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            _ = RemoveProp(hwnd, ModePropertyName);
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
}
