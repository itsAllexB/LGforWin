using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using LGforWin.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace LGforWin.Views;

public sealed partial class MainWindow : Window
{
    private const int MinWidth = 900;
    private const int MinHeight = 600;

    public MainViewModel VM { get; }

    private bool _allowClose;
    private SUBCLASSPROC? _subclassProc; // kept alive to avoid GC

    public MainWindow(MainViewModel vm)
    {
        VM = vm;
        InitializeComponent();

        Title = "LGforWin";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        // Reopen at the last remembered size (physical px), or the default if none saved yet.
        var w = VM.Settings.WindowWidth > 0 ? VM.Settings.WindowWidth : (int)(900 * scale);
        var h = VM.Settings.WindowHeight > 0 ? VM.Settings.WindowHeight : (int)(680 * scale);
        AppWindow.Resize(new SizeInt32(w, h));

        // Enforce a minimum window size (WinUI AppWindow has no MinSize) via WM_GETMINMAXINFO.
        _subclassProc = SubclassProc;
        SetWindowSubclass(hwnd, _subclassProc, 1, IntPtr.Zero);

        // Mica backdrop + the standard TitleBar control as the custom title bar.
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Keep the system caption buttons readable whenever the effective theme changes —
        // including Windows itself switching light/dark while we follow the system.
        if (Content is FrameworkElement content)
            content.ActualThemeChanged += (_, _) => ApplyCaptionButtonTheme();
        ApplyCaptionButtonTheme();

        var show = new RelayCommand(ShowFromTray);
        TrayIcon.LeftClickCommand = show;
        MenuShow.Command = show;
        MenuQuit.Command = new RelayCommand(Quit);

        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            SaveWindowSize();
            e.Cancel = true;
            AppWindow.Hide();
        };

        // Start on Home.
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        switch (item.Tag as string)
        {
            case "home": ContentFrame.Navigate(typeof(HomePage)); break;
            case "schedule": ContentFrame.Navigate(typeof(SchedulePage)); break;
            case "power": ContentFrame.Navigate(typeof(PowerPage)); break;
            case "hotkeys": ContentFrame.Navigate(typeof(HotkeysPage)); break;
            case "osd": ContentFrame.Navigate(typeof(OsdPage)); break;
            case "settings": ContentFrame.Navigate(typeof(SettingsPage)); break;
        }
    }

    /// <summary>Applies the chosen theme to the window's content (0=system,1=light,2=dark).</summary>
    public void ApplyTheme(int index)
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = index switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        ApplyCaptionButtonTheme();
    }

    // The min/max/close buttons are drawn by the system, and with a custom title bar their
    // colors follow the WINDOWS theme — not the app theme. Pick the app dark while Windows
    // is light and they render dark-on-dark, near invisible. So recolor them to match the
    // app's effective theme whenever it changes.
    private void ApplyCaptionButtonTheme()
    {
        if (Content is not FrameworkElement root) return;
        var titleBar = AppWindow.TitleBar;
        var dark = root.ActualTheme == ElementTheme.Dark;

        Windows.UI.Color Fg(byte a) => dark
            ? Windows.UI.Color.FromArgb(a, 255, 255, 255)
            : Windows.UI.Color.FromArgb(a, 0, 0, 0);

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = Fg(255);
        titleBar.ButtonInactiveForegroundColor = Fg(112);
        titleBar.ButtonHoverForegroundColor = Fg(255);
        titleBar.ButtonHoverBackgroundColor = Fg(24);
        titleBar.ButtonPressedForegroundColor = Fg(255);
        titleBar.ButtonPressedBackgroundColor = Fg(41);
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    private void Quit()
    {
        SaveWindowSize();
        _allowClose = true;
        TrayIcon.Dispose();
        (Application.Current as App)?.ShutdownServices();
        Application.Current.Exit();
    }

    // Remembers the current window size so a normal launch reopens at the same size. Skips
    // maximized/minimized states so we always store a sensible "restored" size.
    private void SaveWindowSize()
    {
        if ((AppWindow.Presenter as OverlappedPresenter)?.State != OverlappedPresenterState.Restored) return;
        VM.Settings.WindowWidth = AppWindow.Size.Width;
        VM.Settings.WindowHeight = AppWindow.Size.Height;
        VM.SaveSettings();
    }

    // ----- Minimum window size via window subclassing -----

    private const uint WM_GETMINMAXINFO = 0x0024;

    private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (uMsg == WM_GETMINMAXINFO)
        {
            var scale = GetDpiForWindow(hWnd) / 96.0;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.x = (int)(MinWidth * scale);
            mmi.ptMinTrackSize.y = (int)(MinHeight * scale);
            Marshal.StructureToPtr(mmi, lParam, false);
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC proc, IntPtr id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
