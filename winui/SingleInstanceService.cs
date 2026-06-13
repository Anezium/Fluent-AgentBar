using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace FluentAgentBar;

internal enum ActivationIntent
{
    ShowSettings,
    ShowFlyout
}

internal sealed class SingleInstanceService : IDisposable
{
    internal const string InstanceKey = "FluentAgentBar.SingleInstance";

    private readonly AppInstance _appInstance;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action<ActivationIntent> _activationRequested;
    private bool _disposed;

    private SingleInstanceService(
        AppInstance appInstance,
        DispatcherQueue dispatcherQueue,
        Action<ActivationIntent> activationRequested)
    {
        _appInstance = appInstance;
        _dispatcherQueue = dispatcherQueue;
        _activationRequested = activationRequested;
        _appInstance.Activated += OnActivated;
    }

    internal static async Task<SingleInstanceStartupResult> RegisterOrRedirectAsync(
        DispatcherQueue dispatcherQueue,
        Action<ActivationIntent> activationRequested)
    {
        // Windows App SDK AppInstance works for this unpackaged WinUI app and
        // avoids carrying forward the legacy C++ mutex name or window-message path.
        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!keyInstance.IsCurrent)
        {
            await keyInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            return SingleInstanceStartupResult.Redirected;
        }

        return SingleInstanceStartupResult.Primary(
            new SingleInstanceService(keyInstance, dispatcherQueue, activationRequested));
    }

    internal static ActivationIntent ParseActivationIntent(IEnumerable<string>? args)
    {
        return ParseExplicitActivationIntent(args) ?? ActivationIntent.ShowSettings;
    }

    internal static ActivationIntent? ParseExplicitActivationIntent(IEnumerable<string>? args)
    {
        if (args is null)
        {
            return null;
        }

        foreach (string arg in args)
        {
            if (arg.Equals("--show-flyout", StringComparison.OrdinalIgnoreCase))
            {
                return ActivationIntent.ShowFlyout;
            }

            if (arg.Equals("--show-settings", StringComparison.OrdinalIgnoreCase))
            {
                return ActivationIntent.ShowSettings;
            }
        }

        return null;
    }

    internal static ActivationIntent ParseActivationIntent(AppActivationArguments args)
    {
        if (args.Data is ILaunchActivatedEventArgs launchArgs)
        {
            return ParseActivationIntent(SplitCommandLineArguments(launchArgs.Arguments));
        }

        return ActivationIntent.ShowSettings;
    }

    private static string[] SplitCommandLineArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        return arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        ActivationIntent intent = ParseActivationIntent(args);
        _dispatcherQueue.TryEnqueue(() => _activationRequested(intent));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _appInstance.Activated -= OnActivated;
    }
}

internal sealed record SingleInstanceStartupResult(bool IsPrimary, SingleInstanceService? Service)
{
    public static SingleInstanceStartupResult Redirected { get; } = new(false, null);

    public static SingleInstanceStartupResult Primary(SingleInstanceService service)
    {
        return new SingleInstanceStartupResult(true, service);
    }
}
