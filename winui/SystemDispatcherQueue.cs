using System.Runtime.InteropServices;

namespace FluentAgentBar;

// Windows.UI.Composition objects (Compositor, backdrop controllers) require a
// Windows.System.DispatcherQueue on the thread; WinUI 3 only provides the
// Microsoft.UI.Dispatching one, so create the system controller on demand.
internal static class SystemDispatcherQueue
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        ref IntPtr dispatcherQueueController);

    private static IntPtr _controller;

    public static void Ensure()
    {
        if (_controller != IntPtr.Zero ||
            Windows.System.DispatcherQueue.GetForCurrentThread() is not null)
        {
            return;
        }

        DispatcherQueueOptions options = new()
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2,    // DQTYPE_THREAD_CURRENT
            ApartmentType = 2  // DQTAT_COM_STA
        };
        CreateDispatcherQueueController(options, ref _controller);
    }
}
