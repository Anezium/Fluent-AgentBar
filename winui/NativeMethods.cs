using System.Runtime.InteropServices;
using System.Text;

namespace FluentAgentBar;

internal static partial class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;
    internal const int GWL_STYLE = -16;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int HWND_TOP = 0;
    internal const int HWND_TOPMOST = -1;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const int WS_EX_APPWINDOW = 0x00040000;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_COLORKEY = 0x00000001;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_CLIPCHILDREN = 0x02000000;
    internal const int WS_CLIPSIBLINGS = 0x04000000;
    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_CAPTION = 0x00C00000;
    internal const int MONITOR_DEFAULTTONEAREST = 2;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWCP_ROUND = 2;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;
    private const int GWLP_HWNDPARENT = -8;
    private const int GW_HWNDNEXT = 2;
    private const int FullscreenTolerance = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindow(string className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo monitorInfo);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetTopWindow(IntPtr parentHandle);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hwnd, int insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    internal static void ExtendFrameIntoClientArea(IntPtr hwnd)
    {
        Margins margins = new() { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial int GetPixel(IntPtr hdc, int x, int y);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    internal static IntPtr FindPrimaryTaskbar()
    {
        return FindWindow("Shell_TrayWnd", null);
    }

    internal static IReadOnlyList<TaskbarTarget> FindTaskbars()
    {
        List<TaskbarTarget> targets = [];
        HashSet<IntPtr> seen = [];

        IntPtr primary = FindPrimaryTaskbar();
        if (primary != IntPtr.Zero && seen.Add(primary))
        {
            targets.Add(new TaskbarTarget(primary, IsPrimary: true));
        }

        IntPtr secondary = IntPtr.Zero;
        while (true)
        {
            secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
            if (secondary == IntPtr.Zero)
            {
                break;
            }

            if (seen.Add(secondary))
            {
                targets.Add(new TaskbarTarget(secondary, IsPrimary: false));
            }
        }

        return targets;
    }

    internal static Rect GetPrimaryTaskbarRect()
    {
        IntPtr taskbar = FindPrimaryTaskbar();
        if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out Rect rect))
        {
            return rect;
        }

        return new Rect
        {
            Left = 0,
            Top = 0,
            Right = 640,
            Bottom = 48
        };
    }

    internal static Rect TryGetTrayRect(Rect fallbackTaskbarRect)
    {
        IntPtr taskbar = FindPrimaryTaskbar();
        return TryGetTrayRect(taskbar, fallbackTaskbarRect);
    }

    internal static Rect TryGetTrayRect(IntPtr taskbar, Rect fallbackTaskbarRect)
    {
        if (taskbar == IntPtr.Zero)
        {
            return fallbackTaskbarRect;
        }

        IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        return tray != IntPtr.Zero && GetWindowRect(tray, out Rect rect) ? rect : fallbackTaskbarRect;
    }

    internal static Rect GetMonitorRectForWindow(IntPtr hwnd)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
        {
            return info.Monitor;
        }

        return new Rect
        {
            Left = 0,
            Top = 0,
            Right = 1280,
            Bottom = 720
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public IntPtr RegionBlur;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);

    // Classic WinUI 3 transparency recipe: enabling blur-behind with an empty
    // region makes DWM honor per-pixel alpha from the composition surface, so
    // a transparent backdrop really shows what is behind the window.
    internal static void EnableTransparentComposition(IntPtr hwnd)
    {
        IntPtr emptyRegion = CreateRoundRectRgn(-2, -2, -1, -1, 0, 0);
        try
        {
            DwmBlurBehind blurBehind = new()
            {
                Flags = 0x1 | 0x2, // DWM_BB_ENABLE | DWM_BB_BLURREGION
                Enable = true,
                RegionBlur = emptyRegion,
                TransitionOnMaximized = false
            };
            _ = DwmEnableBlurBehindWindow(hwnd, ref blurBehind);
        }
        finally
        {
            if (emptyRegion != IntPtr.Zero)
            {
                DeleteObject(emptyRegion);
            }
        }
    }

    internal static void SetTopMostNoActivate(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, [MarshalAs(UnmanagedType.Bool)] bool attach);

    // This process only owns no-activate tool windows, so it never holds the
    // foreground lock and plain SetForegroundWindow is refused; briefly attach
    // to the current foreground thread's input queue to borrow that right.
    internal static void ForceForeground(IntPtr hwnd)
    {
        IntPtr foreground = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = foreground != IntPtr.Zero
            ? GetWindowThreadProcessId(foreground, out _)
            : 0;

        bool attached = foregroundThread != 0 &&
            foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    internal static bool IsWindowAbove(IntPtr hwnd, IntPtr comparisonHwnd)
    {
        IntPtr current = GetTopWindow(IntPtr.Zero);
        while (current != IntPtr.Zero)
        {
            if (current == hwnd)
            {
                return true;
            }

            if (current == comparisonHwnd)
            {
                return false;
            }

            current = GetWindow(current, GW_HWNDNEXT);
        }

        return false;
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

        Rect monitorRect = GetMonitorRectForWindow(referenceHwnd);
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

    internal static void AddExtendedStyle(IntPtr hwnd, int style)
    {
        nint current = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, current | style);
    }

    internal static void RemoveExtendedStyle(IntPtr hwnd, int style)
    {
        nint current = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, current & ~style);
    }

    internal static void AddStyle(IntPtr hwnd, int style)
    {
        nint current = GetWindowLongPtr(hwnd, GWL_STYLE);
        SetWindowLongPtr(hwnd, GWL_STYLE, current | style);
    }

    internal static void RemoveStyle(IntPtr hwnd, int style)
    {
        nint current = GetWindowLongPtr(hwnd, GWL_STYLE);
        SetWindowLongPtr(hwnd, GWL_STYLE, current & ~style);
    }

    internal static void SetWindowOwner(IntPtr hwnd, IntPtr owner)
    {
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, owner);
    }

    internal static void SetRoundedCorners(IntPtr hwnd)
    {
        int preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    internal static void RemoveWindowBorder(IntPtr hwnd)
    {
        int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    internal static void SetWindowBorderColor(IntPtr hwnd, byte red, byte green, byte blue)
    {
        int colorRef = red | (green << 8) | (blue << 16);
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }

    internal static void SetImmersiveDarkMode(IntPtr hwnd, bool enabled)
    {
        int value = enabled ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    internal static bool TryGetScreenPixel(int x, int y, out int colorRef)
    {
        colorRef = 0;
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int pixel = GetPixel(dc, x, y);
            if (pixel == -1)
            {
                return false;
            }

            colorRef = pixel;
            return true;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }

    internal static void SetRoundedRegion(IntPtr hwnd, int width, int height, int radius)
    {
        IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);
        if (region == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region);
        }
    }

    internal static bool IsAnyMouseButtonDown()
    {
        return IsKeyDown(VK_LBUTTON) || IsKeyDown(VK_RBUTTON) || IsKeyDown(VK_MBUTTON);
    }

    internal static bool IsLeftMouseButtonDown()
    {
        return IsKeyDown(VK_LBUTTON);
    }

    internal static bool IsRightMouseButtonDown()
    {
        return IsKeyDown(VK_RBUTTON);
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }
}
