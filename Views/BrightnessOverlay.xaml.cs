using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using LGforWin.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace LGforWin.Views;

/// <summary>
/// A small borderless, always-on-top OSD (like the Windows volume/brightness flyout) that
/// briefly shows the brightness when it's changed via global hotkeys with the main window hidden.
///
/// Rebuilt to mirror the Win11 system OSD: an acrylic pill that slides + fades in as a unit,
/// anchored to a configurable screen corner on a configurable monitor, with a tunable timeout.
/// </summary>
public sealed partial class BrightnessOverlay : Window
{
    // Logical (DIP) metrics — converted to physical pixels per the monitor's DPI on show.
    private const double WidthDip = 210;
    private const double HeightDip = 52;          // icon + bar + value
    private const double HeightWithNameDip = 70;  // + TV name line
    private const double EdgeMarginDip = 16;       // gap from screen edges (matches FluentFlyout)
    private const double BottomMarginDip = 8;      // gap above the taskbar (lower, ~matches the Win11 volume OSD)
    private const double SlideDip = 20;            // travel of the slide-in/out
    private const double MsgWidthDip = 300;        // the "no TV on this screen" prompt is wider (has text)
    private const double MsgHeightDip = 68;

    private const int FadeInMs = 250;
    private const int FadeOutMs = 190;

    private readonly DispatcherTimer _hideTimer;
    private readonly Stopwatch _tweenClock = new();
    private readonly IntPtr _hwnd;

    // Tween state.
    private double _fromX, _fromY, _toX, _toY, _fromOpacity, _toOpacity, _tweenDurationMs;
    private bool _tweenEaseOut;
    private bool _rendering; // subscribed to CompositionTarget.Rendering
    private Action? _tweenComplete;

    // Current geometry, kept so the hide timer can compute the exit slide.
    private int _finalX, _finalY, _slidePx;
    private bool _anchoredTop;

    private bool _visible;   // window is shown (may be fading out)
    private bool _hiding;    // a fade-out tween is in progress

    public BrightnessOverlay()
    {
        InitializeComponent();

        // A normal Win11 window, keeping its native border, rounded corners and drop shadow — just
        // with the title bar (and thus the min/max/close buttons) removed. Because it's an ordinary
        // opaque window (no transparency), it renders identically on every display; the earlier
        // transparent-window approach leaked a thin rectangle on secondary displays. And the corners
        // are DWM's own — correct radius and anti-aliasing, which hand-drawn XAML corners can't match.
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false); // border + native rounded corners, no title bar
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        // Acrylic fills the window; DWM clips it to the native rounded corners. Always-active so it
        // stays lit even though the OSD window never takes focus.
        SystemBackdrop = new Controls.AlwaysActiveDesktopAcrylicBackdrop();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Non-activating tool window (like the system OSD): never takes focus, so no keyboard
        // focus rectangle is drawn and it never steals focus from the active app.
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); BeginHide(); };
    }

    /// <summary>Matches the OSD to the app theme (0 = system, 1 = light, 2 = dark).</summary>
    public void ApplyTheme(int index)
    {
        // Drives the acrylic tint (AlwaysActiveDesktopAcrylicBackdrop follows Root's ActualTheme)
        // and the text ThemeResources.
        Root.RequestedTheme = index switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        // Tint the native window border/shadow to match the effective theme so it doesn't read as
        // a light frame in dark mode (or vice-versa).
        var dark = Root.ActualTheme == ElementTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    /// <summary>Shows (or refreshes) the brightness OSD, sliding + fading in per the settings.</summary>
    /// <param name="screenId">Stable id of the adjusted TV's display (for the "TV's screen" mode).</param>
    /// <param name="allowFallback">When the TV's screen can't be resolved, fall back to primary
    /// instead of not showing (used by the Preview button so it always shows something).</param>
    public void ShowBrightness(string? tvName, int value, OsdSettings osd, string? screenId, bool allowFallback = false)
    {
        // Resolve the target work area first: if the mode is "the adjusted TV's screen" and that
        // screen isn't paired/connected, we deliberately don't show anything (no fallback).
        var resolved = ResolveWorkArea(osd.Monitor, screenId, allowFallback);
        if (resolved is null) return;

        value = Math.Clamp(value, 0, 100);
        ValueText.Text = value.ToString();
        FillColumn.Width = new GridLength(value, GridUnitType.Star);
        RestColumn.Width = new GridLength(100 - value, GridUnitType.Star);

        var showName = osd.ShowTvName && !string.IsNullOrWhiteSpace(tvName);
        NameText.Text = showName ? tvName : "";
        NameText.Visibility = showName ? Visibility.Visible : Visibility.Collapsed;

        BrightnessContent.Visibility = Visibility.Visible;
        MessageContent.Visibility = Visibility.Collapsed;
        ShowAt(resolved.Value, osd.Position, WidthDip, showName ? HeightWithNameDip : HeightDip, osd.TimeoutSeconds);
    }

    /// <summary>
    /// Shows the "no TV on this screen — pair your TVs" prompt on the cursor's screen, WITHOUT
    /// changing any brightness. Used when a hotkey targets the cursor's TV but that screen isn't
    /// paired to one.
    /// </summary>
    public void ShowUnpaired(OsdSettings osd)
    {
        MsgTitle.Text = "No TV on this screen";
        MsgSubtitle.Text = "Pair your TVs to their screens on the Home page to control them here.";
        BrightnessContent.Visibility = Visibility.Collapsed;
        MessageContent.Visibility = Visibility.Visible;
        ShowAt(CursorWorkArea(), osd.Position, MsgWidthDip, MsgHeightDip, osd.TimeoutSeconds);
    }

    // Common show path: place on the work area, size to content (per-monitor DPI), anchor, then
    // slide + fade in (or snap if already up).
    private void ShowAt(RectInt32 work, int position, double widthDip, double heightDip, double timeoutSeconds)
    {
        // Adopt the target monitor's DPI by moving the window onto it before measuring (DPI is
        // per-monitor; reading it after the move gives the right scale).
        AppWindow.Move(new PointInt32(work.X, work.Y));

        var scale = GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        var size = new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale));
        AppWindow.Resize(size);

        (_finalX, _finalY, _anchoredTop) = Anchor(position, work, size, scale);
        _slidePx = (int)(SlideDip * scale);

        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0.5, 5.0));

        if (!_visible)
        {
            // Pre-place just off the resting spot (below for bottom anchors, above for top) and fade up.
            Root.Opacity = 0;
            AppWindow.Move(new PointInt32(_finalX, _anchoredTop ? _finalY - _slidePx : _finalY + _slidePx));
            AppWindow.Show(activateWindow: false); // surface without stealing focus
            _visible = true;
            _hiding = false;
            BeginTween(_finalX, _finalY, 1.0, FadeInMs, easeOut: true, onComplete: null);
        }
        else if (_hiding)
        {
            // Interrupted mid fade-out: glide back to fully visible from wherever we are.
            _hiding = false;
            BeginTween(_finalX, _finalY, 1.0, FadeInMs, easeOut: true, onComplete: null);
        }
        else
        {
            // Already up: snap to the (possibly new) anchor without re-running the entrance.
            StopTween();
            AppWindow.Move(new PointInt32(_finalX, _finalY));
            Root.Opacity = 1;
        }

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void BeginHide()
    {
        _hiding = true;
        var exitY = _anchoredTop ? _finalY - _slidePx : _finalY + _slidePx;
        BeginTween(_finalX, exitY, 0.0, FadeOutMs, easeOut: false, onComplete: () =>
        {
            if (!_hiding) return; // a re-show interrupted the hide
            AppWindow.Hide();
            _visible = false;
            _hiding = false;
        });
    }

    // ----- Slide + fade tween (single clock drives opacity and position) -----

    private void BeginTween(int toX, int toY, double toOpacity, int durationMs, bool easeOut, Action? onComplete)
    {
        _fromX = AppWindow.Position.X;
        _fromY = AppWindow.Position.Y;
        _fromOpacity = Root.Opacity;
        _toX = toX;
        _toY = toY;
        _toOpacity = toOpacity;
        _tweenDurationMs = durationMs;
        _tweenEaseOut = easeOut;
        _tweenComplete = onComplete;
        _tweenClock.Restart();
        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
    }

    private void StopTween()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
        _tweenClock.Reset();
        _tweenComplete = null;
    }

    // Driven by the composition clock (one callback per rendered frame = monitor refresh rate),
    // so the slide + fade stay smooth instead of stepping at a fixed timer interval.
    private void OnRendering(object? sender, object e)
    {
        var t = Math.Clamp(_tweenClock.Elapsed.TotalMilliseconds / _tweenDurationMs, 0.0, 1.0);
        // Cubic ease (out for entrance = decelerate, in for exit = accelerate).
        var k = _tweenEaseOut ? 1 - Math.Pow(1 - t, 3) : Math.Pow(t, 3);

        Root.Opacity = _fromOpacity + (_toOpacity - _fromOpacity) * k;
        var x = (int)Math.Round(_fromX + (_toX - _fromX) * k);
        var y = (int)Math.Round(_fromY + (_toY - _fromY) * k);
        AppWindow.Move(new PointInt32(x, y));

        if (t >= 1.0)
        {
            if (_rendering)
            {
                CompositionTarget.Rendering -= OnRendering;
                _rendering = false;
            }
            _tweenClock.Reset();
            var done = _tweenComplete;
            _tweenComplete = null;
            done?.Invoke();
        }
    }

    // ----- Positioning -----

    /// <summary>
    /// Resolves the work area (physical px) to show the OSD on, per the chosen mode:
    /// 0 = primary, 1 = the adjusted TV's paired screen, 2 = the screen under the cursor.
    /// Returns null for mode 1 when the TV's screen isn't paired/connected (and no fallback) —
    /// the caller then shows nothing.
    /// </summary>
    private RectInt32? ResolveWorkArea(int monitorMode, string? screenId, bool allowFallback)
    {
        switch (monitorMode)
        {
            case 2: // screen under the cursor
                if (GetCursorPos(out var pt))
                {
                    var da = DisplayArea.GetFromPoint(new PointInt32(pt.x, pt.y), DisplayAreaFallback.Nearest);
                    if (da is not null) return da.WorkArea;
                }
                return DisplayArea.Primary.WorkArea;

            case 1: // the adjusted TV's screen
                var monitors = MonitorService.List();
                if (!string.IsNullOrEmpty(screenId))
                {
                    var paired = monitors.FirstOrDefault(m => m.Id == screenId);
                    if (paired is not null) return paired.WorkArea;
                }
                // Only one display connected → it's unambiguously the one to use (not a fallback).
                if (monitors.Count == 1) return monitors[0].WorkArea;
                return allowFallback ? DisplayArea.Primary.WorkArea : null;

            default: // 0 = primary
                return DisplayArea.Primary.WorkArea;
        }
    }

    /// <summary>Work area of the screen the cursor is on (falls back to primary).</summary>
    private RectInt32 CursorWorkArea()
    {
        if (GetCursorPos(out var pt))
        {
            var da = DisplayArea.GetFromPoint(new PointInt32(pt.x, pt.y), DisplayAreaFallback.Nearest);
            if (da is not null) return da.WorkArea;
        }
        return DisplayArea.Primary.WorkArea;
    }

    /// <summary>
    /// Computes the resting position (physical px) for an anchor, and whether it's a top anchor.
    /// Positions: 0 bottom-centre, 1 bottom-left, 2 bottom-right, 3 top-centre, 4 top-left, 5 top-right.
    /// </summary>
    private static (int x, int y, bool top) Anchor(int position, RectInt32 work, SizeInt32 size, double scale)
    {
        var edge = (int)(EdgeMarginDip * scale);
        var bottom = (int)(BottomMarginDip * scale);
        var top = position is 3 or 4 or 5;

        var x = position switch
        {
            1 or 4 => work.X + edge,                                  // left
            2 or 5 => work.X + work.Width - size.Width - edge,        // right
            _ => work.X + (work.Width - size.Width) / 2              // centre (0, 3)
        };
        var y = top
            ? work.Y + edge
            : work.Y + work.Height - size.Height - bottom;

        return (x, y, top);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
