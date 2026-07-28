using System.Runtime.InteropServices;

namespace LGforWin.Services;

/// <summary>
/// Surfaces the Windows power lifecycle as events, by subclassing the (always-alive,
/// possibly hidden) main window: PC sleep/resume, the console display turning off/on
/// (idle timeout), and the session ending (shutdown / restart / sign-out).
/// </summary>
public sealed class PowerEventService : IDisposable
{
    /// <summary>The PC is about to sleep or hibernate. Handlers get ~2 s — send fast, synchronously.</summary>
    public event Action? Suspending;

    /// <summary>The PC resumed from sleep or hibernation.</summary>
    public event Action? Resumed;

    /// <summary>Windows turned the displays off (idle timeout) / back on. True = on.</summary>
    public event Action<bool>? DisplayStateChanged;

    /// <summary>The session is ending: shutdown, restart or sign-out. Handlers get a few seconds.</summary>
    public event Action? SessionEnding;

    private readonly IntPtr _hwnd;
    private readonly SUBCLASSPROC _proc; // kept alive to avoid GC of the delegate
    private IntPtr _displayNotification;
    private bool _lastDisplayOn = true; // Windows delivers the current state on registration; only raise transitions

    public PowerEventService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        SetWindowSubclass(hwnd, _proc, SubclassId, IntPtr.Zero);
        var guid = GuidConsoleDisplayState;
        _displayNotification = RegisterPowerSettingNotification(hwnd, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        switch (uMsg)
        {
            case WM_POWERBROADCAST:
                switch ((int)wParam)
                {
                    case PBT_APMSUSPEND:
                        Log.Write("power: suspending");
                        Suspending?.Invoke();
                        break;
                    case PBT_APMRESUMEAUTOMATIC:
                        Log.Write("power: resumed");
                        Resumed?.Invoke();
                        break;
                    case PBT_POWERSETTINGCHANGE:
                        OnPowerSettingChange(lParam);
                        break;
                }
                break;

            case WM_ENDSESSION when wParam != IntPtr.Zero:
                Log.Write("power: session ending");
                SessionEnding?.Invoke();
                break;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void OnPowerSettingChange(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;
        var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
        if (setting.PowerSetting != GuidConsoleDisplayState) return;

        // Data: 0 = off, 1 = on, 2 = dimmed (ignored).
        var on = setting.Data switch { 0 => false, 1 => true, _ => _lastDisplayOn };
        if (on == _lastDisplayOn) return;
        _lastDisplayOn = on;
        Log.Write($"power: displays {(on ? "on" : "off")}");
        DisplayStateChanged?.Invoke(on);
    }

    public void Dispose()
    {
        if (_displayNotification != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_displayNotification);
            _displayNotification = IntPtr.Zero;
        }
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
    }

    // ----- Win32 interop -----

    private static readonly IntPtr SubclassId = new(2); // MainWindow's min-size subclass uses 1

    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_ENDSESSION = 0x0016;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    private static readonly Guid GuidConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data; // first byte(s) of the variable-length payload; enough for display state
    }

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC proc, IntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC proc, IntPtr id);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
