using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WpfWidgetRuntimeVerifier;

internal static class Program
{
    private const string ModePropertyName = "FluentAgentBar.WpfWidget.Mode";
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int MkLButton = 0x0001;
    private const int DebugExitMessage = 0x8000 + 0x34A;
    private const int DebugDetachMessage = 0x8000 + 0x34B;
    private const uint MaximumAllowed = 0x02000000;
    private static readonly List<CheckResult> Checks = [];
    private static readonly List<string> Events = [];

    [STAThread]
    private static int Main(string[] args)
    {
        _ = Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        DesktopAttachment.AttachProcessToInteractiveWindowStation();
        int exitCode = 1;
        Exception? threadFailure = null;
        Thread verificationThread = new(() =>
        {
            try
            {
                DesktopAttachment.AttachCurrentThreadToDefaultDesktop();
                exitCode = RunVerification(args);
            }
            catch (Exception ex)
            {
                threadFailure = ex;
            }
        })
        {
            Name = "WPF widget verifier interactive STA",
            IsBackground = false
        };
        verificationThread.SetApartmentState(ApartmentState.STA);
        verificationThread.Start();
        verificationThread.Join();
        if (threadFailure is not null)
        {
            Console.Error.WriteLine(threadFailure);
            return 1;
        }

        return exitCode;
    }

    private static int RunVerification(string[] args)
    {
        string appPath = RequiredArgument(args, "--app");
        string artifactDirectory = Path.GetFullPath(RequiredArgument(args, "--artifacts"));
        string productionPath = OptionalArgument(args, "--production-path") ?? "(not recorded)";
        Directory.CreateDirectory(artifactDirectory);
        string reportPath = Path.Combine(artifactDirectory, "runtime-report.txt");
        string jsonPath = Path.Combine(artifactDirectory, "runtime-report.json");
        Process? app = null;
        IntPtr widget = IntPtr.Zero;
        IntPtr taskbar = IntPtr.Zero;
        Rect taskbarRect = default;
        Rect pillRect = default;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        bool forcedCleanup = false;

        try
        {
            taskbar = Native.FindWindow("Shell_TrayWnd", null);
            Require(taskbar != IntPtr.Zero, "Shell_TrayWnd was not found.");
            taskbarRect = RequiredRect(taskbar, "Shell_TrayWnd");
            Capture(taskbarRect, Path.Combine(artifactDirectory, "taskbar-before-launch.png"));
            Log($"Interactive taskbar HWND=0x{taskbar.ToInt64():X}; rect={taskbarRect}.");

            ProcessStartInfo startInfo = new(appPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appPath)!
            };
            startInfo.Environment["FLUENTAGENTBAR_ATTACH_DESKTOP"] = "1";
            app = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
            Log($"Launched candidate PID={app.Id}; path={appPath}.");

            widget = WaitForWidget(app.Id, taskbar, TimeSpan.FromSeconds(20));
            Require(widget != IntPtr.Zero, "No FluentAgentBar widget HWND appeared.");
            Rect childRect = RequiredRect(widget, "WPF widget child");
            IntPtr tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            Rect trayRect = tray != IntPtr.Zero ? RequiredRect(tray, "TrayNotifyWnd") : taskbarRect;
            uint dpi = Native.GetDpiForWindow(taskbar);
            pillRect = CalculatePill(taskbarRect, trayRect, dpi);
            IntPtr parent = Native.GetParent(widget);
            long modeProperty = Native.GetProp(widget, ModePropertyName).ToInt64();
            long style = Native.GetWindowLongPtr(widget, GwlStyle).ToInt64();

            AddCheck(
                "child-mode",
                parent == taskbar && modeProperty == 1 && (style & WsChild) != 0,
                $"widget=0x{widget.ToInt64():X}; parent=0x{parent.ToInt64():X}; " +
                $"taskbar=0x{taskbar.ToInt64():X}; modeProperty={modeProperty}; style=0x{style:X}.");

            bool childCoversTaskbar =
                childRect.Left == taskbarRect.Left &&
                childRect.Top == taskbarRect.Top &&
                childRect.Right == taskbarRect.Right &&
                childRect.Bottom == taskbarRect.Bottom;
            bool pillInside =
                pillRect.Left >= taskbarRect.Left &&
                pillRect.Top >= taskbarRect.Top &&
                pillRect.Right <= taskbarRect.Right &&
                pillRect.Bottom <= taskbarRect.Bottom;
            bool leftOfTray = pillRect.Right <= trayRect.Left;
            AddCheck(
                "geometry",
                childCoversTaskbar && pillInside && leftOfTray,
                $"childCoversTaskbar={childCoversTaskbar}; pillInside={pillInside}; " +
                $"leftOfTrayNotify={leftOfTray}; DPI={dpi}; child={childRect}; " +
                $"pill={pillRect}; tray={trayRect}; taskbar={taskbarRect}.");

            Point center = new((pillRect.Left + pillRect.Right) / 2, (pillRect.Top + pillRect.Bottom) / 2);
            IntPtr centerHit = Native.WindowFromPoint(center);
            Native.GetWindowThreadProcessId(centerHit, out uint centerPid);
            AddCheck(
                "hit-test-widget",
                centerPid == (uint)app.Id,
                $"point={center}; hit=0x{centerHit.ToInt64():X}; ownerPid={centerPid}; appPid={app.Id}.");

            Point leftPoint = new(Math.Max(taskbarRect.Left + 2, pillRect.Left - 200), center.Y);
            IntPtr leftHit = Native.WindowFromPoint(leftPoint);
            Native.GetWindowThreadProcessId(leftHit, out uint leftPid);
            AddCheck(
                "hit-test-passthrough",
                leftPid != (uint)app.Id,
                $"point={leftPoint}; hit=0x{leftHit.ToInt64():X}; ownerPid={leftPid}; appPid={app.Id}.");

            string liveScreenshot = Path.Combine(artifactDirectory, "taskbar-wpf-child.png");
            Capture(taskbarRect, liveScreenshot);
            PixelEvidence pixels = CompareTaskbarImages(
                Path.Combine(artifactDirectory, "taskbar-before-launch.png"),
                liveScreenshot,
                taskbarRect,
                pillRect);
            AddCheck(
                "pixels",
                pixels.PillChangedPixels > 20 && pixels.OutsideBlackRatio < 0.20,
                $"pillChangedPixels={pixels.PillChangedPixels}; " +
                $"outsideBlackRatio={pixels.OutsideBlackRatio:P2}; screenshot={liveScreenshot}.");

            uint foregroundPidBefore = ProcessIdForWindow(Native.GetForegroundWindow());
            PostLeftClick(widget, center.X - childRect.Left, center.Y - childRect.Top);
            IntPtr flyout = WaitForVisibleTopLevelWindow(app.Id, TimeSpan.FromSeconds(5));
            uint foregroundPidAfter = ProcessIdForWindow(Native.GetForegroundWindow());
            bool opened = flyout != IntPtr.Zero;
            bool flyoutAnchored = false;
            Rect flyoutRect = default;
            if (opened)
            {
                Thread.Sleep(500);
                flyoutRect = RequiredRect(flyout, "flyout");
                flyoutAnchored = taskbarRect.Width >= taskbarRect.Height
                    ? Math.Abs(flyoutRect.Right - pillRect.Right) <= 2 &&
                        (flyoutRect.Bottom <= taskbarRect.Top || flyoutRect.Top >= taskbarRect.Bottom)
                    : Math.Abs(flyoutRect.Bottom - pillRect.Bottom) <= 2 &&
                        (flyoutRect.Right <= taskbarRect.Left || flyoutRect.Left >= taskbarRect.Right);
                Capture(flyoutRect, Path.Combine(artifactDirectory, "flyout-open.png"));
            }

            PostLeftClick(widget, center.X - childRect.Left, center.Y - childRect.Top);
            bool closed = WaitUntil(
                () => FindVisibleTopLevelWindow(app.Id) == IntPtr.Zero,
                TimeSpan.FromSeconds(5));
            AddCheck(
                "flyout-toggle",
                opened && flyoutAnchored && closed,
                $"opened={opened}; flyout=0x{flyout.ToInt64():X}; rect={flyoutRect}; " +
                $"anchoredToPill={flyoutAnchored}; closedAgain={closed}.");
            AddCheck(
                "no-activation",
                foregroundPidAfter != (uint)app.Id && foregroundPidAfter == foregroundPidBefore,
                $"foregroundPidBefore={foregroundPidBefore}; foregroundPidAfter={foregroundPidAfter}; appPid={app.Id}.");

            PostLeftClick(widget, center.X - childRect.Left, center.Y - childRect.Top);
            bool rapidToggleOpened = WaitForVisibleTopLevelWindow(app.Id, TimeSpan.FromSeconds(5)) != IntPtr.Zero;
            Thread.Sleep(200);
            PostLeftClick(widget, center.X - childRect.Left, center.Y - childRect.Top, settleMilliseconds: 0);
            Thread.Sleep(40);
            PostLeftClick(widget, center.X - childRect.Left, center.Y - childRect.Top, settleMilliseconds: 0);
            StringBuilder rapidVisibility = new();
            bool rapidToggleStayedOpen = false;
            for (int sample = 0; sample < 12; sample++)
            {
                Thread.Sleep(20);
                rapidToggleStayedOpen = FindVisibleTopLevelWindow(app.Id) != IntPtr.Zero;
                rapidVisibility.Append(rapidToggleStayedOpen ? '1' : '0');
            }
            AddCheck(
                "rapid-toggle-reopen",
                rapidToggleOpened && rapidToggleStayedOpen,
                $"opened={rapidToggleOpened}; stayedOpenAfterCloseReopen={rapidToggleStayedOpen}; " +
                $"visibility20msSamples={rapidVisibility}.");

            PostLeftClick(widget, center.X - childRect.Left, center.Y - childRect.Top);
            bool closedAfterRapidToggle = WaitUntil(
                () => FindVisibleTopLevelWindow(app.Id) == IntPtr.Zero,
                TimeSpan.FromSeconds(5));
            AddCheck(
                "rapid-toggle-cleanup",
                closedAfterRapidToggle,
                $"closed={closedAfterRapidToggle}.");

            IntPtr detachedWidget = widget;
            Native.PostMessage(detachedWidget, DebugDetachMessage, IntPtr.Zero, IntPtr.Zero);
            IntPtr replacementWidget = WaitForReplacementWidget(
                app.Id,
                taskbar,
                detachedWidget,
                TimeSpan.FromSeconds(10));
            bool detachedWidgetDestroyed = WaitUntil(
                () => !Native.IsWindow(detachedWidget),
                TimeSpan.FromSeconds(5));
            bool widgetRecovered =
                replacementWidget != IntPtr.Zero &&
                Native.GetParent(replacementWidget) == taskbar &&
                Native.GetProp(replacementWidget, ModePropertyName).ToInt64() == 1;
            AddCheck(
                "target-loss-recovery",
                detachedWidgetDestroyed && widgetRecovered,
                $"detached=0x{detachedWidget.ToInt64():X}; destroyed={detachedWidgetDestroyed}; " +
                $"replacement=0x{replacementWidget.ToInt64():X}; recovered={widgetRecovered}.");
            if (replacementWidget != IntPtr.Zero)
            {
                widget = replacementWidget;
            }

            Native.PostMessage(widget, DebugExitMessage, IntPtr.Zero, IntPtr.Zero);
            bool exited = app.WaitForExit(10000);
            AddCheck(
                "clean-exit",
                exited && !Native.IsWindow(widget),
                $"processExited={exited}; widgetDestroyed={!Native.IsWindow(widget)}; " +
                $"exitCode={(exited ? app.ExitCode : null)}.");

            if (!exited)
            {
                forcedCleanup = true;
                app.Kill(entireProcessTree: true);
                app.WaitForExit(5000);
            }

            Capture(taskbarRect, Path.Combine(artifactDirectory, "taskbar-after-exit.png"));
            IntPtr postExitHit = Native.WindowFromPoint(center);
            uint postExitPid = ProcessIdForWindow(postExitHit);
            AddCheck(
                "taskbar-pristine",
                postExitPid != (uint)app.Id && !Native.IsWindow(widget),
                $"centerHitAfterExit=0x{postExitHit.ToInt64():X}; ownerPid={postExitPid}; " +
                $"widgetDestroyed={!Native.IsWindow(widget)}; screenshot=taskbar-after-exit.png.");
        }
        catch (Exception ex)
        {
            Log($"Verification exception: {ex}");
            AddCheck("verification-sequence", false, ex.ToString());
        }
        finally
        {
            if (app is { HasExited: false })
            {
                forcedCleanup = true;
                try
                {
                    app.Kill(entireProcessTree: true);
                    app.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log($"Forced cleanup failed: {ex}");
                }
            }

            WriteReports(
                reportPath,
                jsonPath,
                appPath,
                productionPath,
                started,
                DateTimeOffset.UtcNow,
                forcedCleanup);
        }

        return Checks.Count >= 9 && Checks.All(check => check.Passed) ? 0 : 1;
    }

    private static IntPtr WaitForWidget(int pid, IntPtr taskbar, TimeSpan timeout)
    {
        IntPtr result = IntPtr.Zero;
        WaitUntil(
            () =>
            {
                foreach (IntPtr child in EnumerateChildWindows(taskbar))
                {
                    Native.GetWindowThreadProcessId(child, out uint ownerPid);
                    if (ownerPid == (uint)pid && Native.GetProp(child, ModePropertyName) != IntPtr.Zero)
                    {
                        result = child;
                        return true;
                    }
                }

                foreach (IntPtr topLevel in EnumerateTopLevelWindows())
                {
                    Native.GetWindowThreadProcessId(topLevel, out uint ownerPid);
                    if (ownerPid == (uint)pid && Native.GetProp(topLevel, ModePropertyName) != IntPtr.Zero)
                    {
                        result = topLevel;
                        return true;
                    }
                }

                return false;
            },
            timeout);
        return result;
    }

    private static IntPtr WaitForReplacementWidget(
        int pid,
        IntPtr taskbar,
        IntPtr previousWidget,
        TimeSpan timeout)
    {
        IntPtr result = IntPtr.Zero;
        WaitUntil(
            () =>
            {
                foreach (IntPtr child in EnumerateChildWindows(taskbar))
                {
                    Native.GetWindowThreadProcessId(child, out uint ownerPid);
                    if (child != previousWidget &&
                        ownerPid == (uint)pid &&
                        Native.GetProp(child, ModePropertyName) != IntPtr.Zero)
                    {
                        result = child;
                        return true;
                    }
                }

                return false;
            },
            timeout);
        return result;
    }

    private static Rect CalculatePill(Rect taskbar, Rect tray, uint dpi)
    {
        double scale = Math.Max(1.0, (dpi == 0 ? 96u : dpi) / 96d);
        int width = (int)Math.Round(304 * scale);
        int height = (int)Math.Round(40 * scale);
        int gap = (int)Math.Round(8 * scale);
        bool horizontal = taskbar.Width >= taskbar.Height;
        if (horizontal)
        {
            int trayLeft = tray.Left > 0 ? tray.Left : taskbar.Right;
            int x = taskbar.Left + Math.Max(gap, trayLeft - taskbar.Left - width - gap);
            int y = taskbar.Top + Math.Max(0, (taskbar.Height - height) / 2);
            return new Rect(x, y, x + width, y + height);
        }

        int trayTop = tray.Top > 0 ? tray.Top : taskbar.Bottom;
        int verticalX = taskbar.Left + Math.Max(0, (taskbar.Width - width) / 2);
        int verticalY = taskbar.Top + Math.Max(gap, trayTop - taskbar.Top - height - gap);
        return new Rect(verticalX, verticalY, verticalX + width, verticalY + height);
    }

    private static void PostLeftClick(IntPtr hwnd, int x, int y, int settleMilliseconds = 300)
    {
        IntPtr lParam = new((y << 16) | (x & 0xFFFF));
        Native.PostMessage(hwnd, WmMouseMove, IntPtr.Zero, lParam);
        Native.PostMessage(hwnd, WmLButtonDown, new IntPtr(MkLButton), lParam);
        Native.PostMessage(hwnd, WmLButtonUp, IntPtr.Zero, lParam);
        if (settleMilliseconds > 0)
        {
            Thread.Sleep(settleMilliseconds);
        }
    }

    private static IntPtr WaitForVisibleTopLevelWindow(int pid, TimeSpan timeout)
    {
        IntPtr result = IntPtr.Zero;
        WaitUntil(
            () =>
            {
                result = FindVisibleTopLevelWindow(pid);
                return result != IntPtr.Zero;
            },
            timeout);
        return result;
    }

    private static IntPtr FindVisibleTopLevelWindow(int pid)
    {
        foreach (IntPtr hwnd in EnumerateTopLevelWindows())
        {
            Native.GetWindowThreadProcessId(hwnd, out uint ownerPid);
            if (ownerPid != (uint)pid ||
                !Native.IsWindowVisible(hwnd) ||
                !Native.GetWindowRect(hwnd, out Rect rect) ||
                rect.IsEmpty ||
                Native.GetProp(hwnd, ModePropertyName) != IntPtr.Zero)
            {
                continue;
            }

            return hwnd;
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<IntPtr> EnumerateChildWindows(IntPtr parent)
    {
        List<IntPtr> windows = [];
        Native.EnumChildWindows(parent, (hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static IEnumerable<IntPtr> EnumerateTopLevelWindows()
    {
        List<IntPtr> windows = [];
        Native.EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return condition();
    }

    private static void Capture(Rect rect, string path)
    {
        using Bitmap bitmap = new(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(
            rect.Left,
            rect.Top,
            0,
            0,
            new Size(rect.Width, rect.Height),
            CopyPixelOperation.SourceCopy);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static PixelEvidence CompareTaskbarImages(
        string beforePath,
        string afterPath,
        Rect taskbar,
        Rect pill)
    {
        using Bitmap before = new(beforePath);
        using Bitmap after = new(afterPath);
        int changed = 0;
        for (int y = pill.Top; y < pill.Bottom; y++)
        {
            for (int x = pill.Left; x < pill.Right; x++)
            {
                Color first = before.GetPixel(x - taskbar.Left, y - taskbar.Top);
                Color second = after.GetPixel(x - taskbar.Left, y - taskbar.Top);
                if (Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B) > 24)
                {
                    changed++;
                }
            }
        }

        int outsideTotal = 0;
        int outsideBlack = 0;
        int outsideRight = Math.Max(taskbar.Left, pill.Left - 20);
        for (int y = taskbar.Top; y < taskbar.Bottom; y++)
        {
            for (int x = taskbar.Left; x < outsideRight; x += 4)
            {
                Color color = after.GetPixel(x - taskbar.Left, y - taskbar.Top);
                outsideTotal++;
                if (color.R <= 5 && color.G <= 5 && color.B <= 5)
                {
                    outsideBlack++;
                }
            }
        }

        return new PixelEvidence(
            changed,
            outsideTotal == 0 ? 0 : outsideBlack / (double)outsideTotal);
    }

    private static Rect RequiredRect(IntPtr hwnd, string name)
    {
        if (!Native.GetWindowRect(hwnd, out Rect rect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetWindowRect failed for {name}.");
        }

        return rect;
    }

    private static uint ProcessIdForWindow(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    private static void AddCheck(string id, bool passed, string detail)
    {
        Checks.Add(new CheckResult(id, passed, detail));
        Log($"{id}: {(passed ? "PASS" : "FAIL")} - {detail}");
    }

    private static void Log(string message)
    {
        Events.Add($"[{DateTimeOffset.Now:O}] {message}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void WriteReports(
        string textPath,
        string jsonPath,
        string appPath,
        string productionPath,
        DateTimeOffset started,
        DateTimeOffset completed,
        bool forcedCleanup)
    {
        bool passed = Checks.Count >= 9 && Checks.All(check => check.Passed);
        StringBuilder text = new();
        text.AppendLine($"VERDICT: {(passed ? "PASS" : "FAIL")}");
        text.AppendLine($"Candidate: {appPath}");
        text.AppendLine($"Recorded production exe: {productionPath}");
        text.AppendLine($"Started (UTC): {started:O}");
        text.AppendLine($"Completed (UTC): {completed:O}");
        text.AppendLine($"Forced cleanup required: {forcedCleanup}");
        text.AppendLine();
        text.AppendLine("Checks");
        text.AppendLine("------");
        foreach (CheckResult check in Checks)
        {
            text.AppendLine($"{check.Id}: {(check.Passed ? "PASS" : "FAIL")} - {check.Detail}");
        }

        text.AppendLine();
        text.AppendLine("Event log");
        text.AppendLine("---------");
        foreach (string entry in Events)
        {
            text.AppendLine(entry);
        }

        File.WriteAllText(textPath, text.ToString(), new UTF8Encoding(false));
        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(
                new
                {
                    verdict = passed ? "PASS" : "FAIL",
                    candidate = appPath,
                    productionPath,
                    started,
                    completed,
                    forcedCleanup,
                    checks = Checks,
                    events = Events
                },
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static string RequiredArgument(string[] args, string name) =>
        OptionalArgument(args, name) ??
        throw new ArgumentException($"Required argument missing: {name}");

    private static string? OptionalArgument(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private sealed record CheckResult(string Id, bool Passed, string Detail);
    private sealed record PixelEvidence(int PillChangedPixels, double OutsideBlackRatio);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Point(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        internal bool IsEmpty => Width <= 0 || Height <= 0;
    }

    private static class DesktopAttachment
    {
        private static IntPtr _windowStation;
        private static IntPtr _desktop;

        internal static void AttachProcessToInteractiveWindowStation()
        {
            _windowStation = Native.OpenWindowStation("WinSta0", false, MaximumAllowed);
            if (_windowStation == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenWindowStation(WinSta0) failed.");
            }

            if (!Native.SetProcessWindowStation(_windowStation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetProcessWindowStation(WinSta0) failed.");
            }

            _desktop = Native.OpenDesktop("Default", 0, false, MaximumAllowed);
            if (_desktop == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenDesktop(Default) failed.");
            }

        }

        internal static void AttachCurrentThreadToDefaultDesktop()
        {
            if (!Native.SetThreadDesktop(_desktop))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadDesktop(Default) failed.");
            }
        }
    }

    private static class Native
    {
        internal delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindowEx(
            IntPtr parent,
            IntPtr childAfter,
            string className,
            string? windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(
            IntPtr parent,
            EnumWindowCallback callback,
            IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowCallback callback, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetParent(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetProp(IntPtr hwnd, string name);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenWindowStation(
            string windowStationName,
            [MarshalAs(UnmanagedType.Bool)] bool inherit,
            uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessWindowStation(IntPtr windowStation);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenDesktop(
            string desktopName,
            uint flags,
            [MarshalAs(UnmanagedType.Bool)] bool inherit,
            uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetThreadDesktop(IntPtr desktop);
    }
}
