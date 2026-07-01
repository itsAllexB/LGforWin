using LGforWin.Services;
using LGforWin.ViewModels;
using LGforWin.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace LGforWin;

public partial class App : Application
{
    private MainWindow? _window;
    private MainViewModel? _vm;
    private HotkeyService? _hotkeys;
    private ScheduleService? _schedules;
    private DispatcherQueue? _dispatcher;
    private BrightnessOverlay? _overlay;

    /// <summary>Shared application view model, accessed by the navigation pages.</summary>
    public MainViewModel? ViewModel => _vm;

    /// <summary>HWND of the main window, needed to initialize file pickers in an unpackaged app.</summary>
    public IntPtr MainWindowHandle =>
        _window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _vm = new MainViewModel(_dispatcher);

        AutostartService.Apply(_vm.Settings.StartWithWindows);

        _hotkeys = new HotkeyService();
        _hotkeys.HotkeyPressed += action =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                var step = _vm!.Settings.HotkeyStep;
                var outcome = _vm.NudgeByCursor(action == HotkeyAction.BrightnessUp ? step : -step);
                switch (outcome.Kind)
                {
                    case HotkeyTargetOutcome.Adjusted:
                        ShowOverlay(outcome.Name, outcome.Value, outcome.ScreenId);
                        break;
                    case HotkeyTargetOutcome.Unpaired:
                        ShowUnpairedOverlay();
                        break;
                }
            });
        };
        _hotkeys.Start((uint)_vm.Settings.HotkeyModifiers);
        _vm.HotkeyModifiersChanged += mods => _hotkeys?.UpdateModifiers(mods);

        _schedules = new ScheduleService(_vm, _dispatcher);
        _schedules.ScheduleApplied += ShowScheduleOverlay;
        _schedules.Start();

        _window = new MainWindow(_vm);
        _vm.ThemeChanged += i => _window?.ApplyTheme(i);
        _window.ApplyTheme(_vm.Settings.ThemeIndex);

        // Manual launches always open the window; the launch-behavior preference applies only to the
        // at-sign-in autostart launch (tagged with --autostart on the Run entry).
        var isAutostart = Environment.GetCommandLineArgs().Contains(AutostartService.AutostartArg);
        ApplyLaunchBehavior(_window, isAutostart ? _vm.Settings.LaunchBehavior : 0);
    }

    // Opens the window per the launch preference (0 = normal window, 1 = minimized, 2 = tray).
    private static void ApplyLaunchBehavior(MainWindow window, int behavior)
    {
        var presenter = window.AppWindow.Presenter as OverlappedPresenter;
        switch (behavior)
        {
            case 2: // system tray: load the window once (so the tray icon initialises), then hide it
                window.Activate();
                window.AppWindow.Hide();
                break;
            case 1: // minimized to the taskbar
                window.Activate();
                presenter?.Minimize();
                break;
            default: // 0 = normal window at its last size
                window.Activate();
                break;
        }
    }

    // Created lazily on the UI thread the first time a hotkey fires.
    private void ShowOverlay(string? tvName, int value, string? screenId)
    {
        if (_vm is null || !_vm.Settings.Osd.Enabled) return;
        _overlay ??= new BrightnessOverlay();
        _overlay.ApplyTheme(_vm.Settings.ThemeIndex);
        _overlay.ShowBrightness(tvName, value, _vm.Settings.Osd, screenId);
    }

    // A hotkey targeted the cursor's TV, but that screen isn't paired to one — prompt to pair
    // instead of silently doing nothing. (Only when the OSD is enabled at all.)
    private void ShowUnpairedOverlay()
    {
        if (_vm is null || !_vm.Settings.Osd.Enabled) return;
        _overlay ??= new BrightnessOverlay();
        _overlay.ApplyTheme(_vm.Settings.ThemeIndex);
        _overlay.ShowUnpaired(_vm.Settings.Osd);
    }

    // Shows the OSD after a schedule applied brightness, if the user opted in. Schedules affect all
    // TVs, so it's one confirmation on the configured screen (always shown — falls back to primary
    // if "TV's screen" can't resolve), named only when there's a single TV.
    private void ShowScheduleOverlay(int value)
    {
        if (_vm is null || !_vm.Settings.Osd.Enabled || !_vm.Settings.Osd.ShowOnSchedule) return;
        var tv = _vm.ActiveTv ?? _vm.Tvs.FirstOrDefault();
        _overlay ??= new BrightnessOverlay();
        _overlay.ApplyTheme(_vm.Settings.ThemeIndex);
        _overlay.ShowBrightness(_vm.Tvs.Count == 1 ? tv?.Name : null, value, _vm.Settings.Osd,
            tv?.Device.PairedScreenId, allowFallback: true);
    }

    /// <summary>Shows the OSD on demand (from the settings page Preview button), ignoring the enable toggle.</summary>
    public void PreviewOverlay()
    {
        if (_vm is null) return;
        var tv = _vm.ActiveTv ?? _vm.Tvs.FirstOrDefault();
        _overlay ??= new BrightnessOverlay();
        _overlay.ApplyTheme(_vm.Settings.ThemeIndex);
        // Preview should always show something even if this TV isn't paired to a screen yet.
        _overlay.ShowBrightness(tv?.Name ?? "LG TV", tv?.Backlight ?? 50, _vm.Settings.Osd,
            tv?.Device.PairedScreenId, allowFallback: true);
    }

    /// <summary>Called when a second launch redirects activation here — surface the window.</summary>
    public void OnRedirected()
    {
        _dispatcher?.TryEnqueue(() => _window?.ShowFromTray());
    }

    /// <summary>Releases hotkeys, sockets and persists state before exit.</summary>
    public void ShutdownServices()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        _schedules?.Dispose();
        _schedules = null;
        _overlay?.Close();
        _overlay = null;
        _vm?.Persist();
        _vm?.Dispose();
        _vm = null;
    }
}
