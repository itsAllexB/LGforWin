using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace LGforWin;

/// <summary>
/// Custom entry point (DisableXamlGeneratedMain=true) so we can enforce a single
/// instance: a second launch redirects activation to the already-running tray app
/// instead of opening a second window.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
            return; // another instance is already running; we redirected to it

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static bool DecideRedirection()
    {
        var keyInstance = AppInstance.FindOrRegisterForKey("LGforWin-single-instance");

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += (_, _) => (App.Current as App)?.OnRedirected();
            return false;
        }

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        keyInstance.RedirectActivationToAsync(activationArgs).AsTask().GetAwaiter().GetResult();
        return true;
    }
}
