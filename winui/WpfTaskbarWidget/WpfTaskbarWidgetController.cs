using System.Windows.Threading;

namespace FluentAgentBar.WpfTaskbarWidget;

public sealed class WpfTaskbarWidgetController : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private readonly TaskCompletionSource _threadExited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskbarWidgetWindow? _window;
    private bool _closing;

    private WpfTaskbarWidgetController(Thread thread, Dispatcher dispatcher)
    {
        _thread = thread;
        _dispatcher = dispatcher;
    }

    public event EventHandler? UsageRequested;
    public event EventHandler<string>? CommandInvoked;
    public event EventHandler? TargetLost;
    public event EventHandler? Closed;

    public static async Task<WpfTaskbarWidgetController> StartAsync(
        IntPtr taskbarHwnd,
        bool isPrimary,
        WidgetVisualState initialState,
        IReadOnlyList<WidgetMenuEntry> initialMenu,
        Action<string>? log = null)
    {
        TaskCompletionSource<WpfTaskbarWidgetController> started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? thread = null;

        thread = new Thread(() =>
        {
            WpfTaskbarWidgetController? controller = null;
            IntPtr attachedDesktop = IntPtr.Zero;
            try
            {
                attachedDesktop = DesktopTestHook.AttachCurrentThreadIfRequested();
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                controller = new WpfTaskbarWidgetController(thread!, dispatcher);
                controller._window = new TaskbarWidgetWindow(
                    taskbarHwnd,
                    isPrimary,
                    initialState,
                    initialMenu,
                    controller.RaiseUsageRequested,
                    controller.RaiseCommandInvoked,
                    controller.RaiseTargetLost,
                    controller.RaiseUnexpectedClose,
                    log);
                controller._window.Start();
                started.TrySetResult(controller);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                try
                {
                    controller?._window?.Close();
                }
                catch (Exception closeException)
                {
                    log?.Invoke($"WPF widget startup cleanup failed: {closeException}");
                }

                started.TrySetException(ex);
            }
            finally
            {
                if (attachedDesktop != IntPtr.Zero)
                {
                    NativeMethods.CloseDesktop(attachedDesktop);
                }

                controller?._threadExited.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = $"Fluent AgentBar WPF taskbar STA 0x{taskbarHwnd.ToInt64():X}"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return await started.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    public TaskbarWidgetRuntimeInfo GetRuntimeInfo()
    {
        if (_window is null)
        {
            throw new InvalidOperationException("The WPF widget has not started.");
        }

        return _dispatcher.CheckAccess()
            ? _window.RuntimeInfo
            : _dispatcher.Invoke(
                () => _window.RuntimeInfo,
                DispatcherPriority.Send,
                CancellationToken.None,
                TimeSpan.FromSeconds(5));
    }

    public void Update(WidgetVisualState state, IReadOnlyList<WidgetMenuEntry> menuEntries)
    {
        if (_closing || _window is null)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            () => _window.Update(state, menuEntries),
            DispatcherPriority.Background);
    }

    public async Task CloseAsync(TimeSpan timeout)
    {
        if (_closing)
        {
            await _threadExited.Task.WaitAsync(timeout).ConfigureAwait(false);
            return;
        }

        _closing = true;
        try
        {
            _ = _dispatcher.BeginInvoke(
                () =>
                {
                    try
                    {
                        _window?.Close();
                    }
                    finally
                    {
                        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    }
                },
                DispatcherPriority.Send);
        }
        catch (TaskCanceledException)
        {
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }

        await _threadExited.Task.WaitAsync(timeout).ConfigureAwait(false);
        if (_thread.IsAlive)
        {
            throw new TimeoutException("The WPF widget STA did not exit after dispatcher shutdown.");
        }
    }

    public void Dispose()
    {
        CloseAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private void RaiseUsageRequested() => UsageRequested?.Invoke(this, EventArgs.Empty);

    private void RaiseCommandInvoked(string commandId) => CommandInvoked?.Invoke(this, commandId);

    private void RaiseTargetLost() => TargetLost?.Invoke(this, EventArgs.Empty);

    private void RaiseUnexpectedClose()
    {
        Closed?.Invoke(this, EventArgs.Empty);
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
    }
}
