using System.Runtime.InteropServices;

namespace LGforWin.Services;

public enum HotkeyAction { BrightnessUp, BrightnessDown }

/// <summary>
/// Registers global brightness hotkeys (default Ctrl+Alt+Up / Ctrl+Alt+Down). Uses a
/// dedicated thread with a null-HWND RegisterHotKey, so WM_HOTKEY is delivered straight
/// to this thread's message queue — no window subclassing of the WinUI window required.
/// Events are raised on the hotkey thread; marshal to the UI thread in the handler.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_APP_REREGISTER = 0x8000; // WM_APP: posted to re-register with new modifiers
    private const int VK_UP = 0x26;
    private const int VK_DOWN = 0x28;

    private const int IdUp = 1;
    private const int IdDown = 2;

    private Thread? _thread;
    private uint _threadId;
    private volatile uint _modifiers; // MOD_* bitmask (no MOD_NOREPEAT); 0 = don't register (no bare arrows)
    private readonly ManualResetEventSlim _ready = new(false);

    public event Action<HotkeyAction>? HotkeyPressed;

    /// <summary>Starts the hotkey thread, registering the arrow keys with the given modifier bitmask.</summary>
    public void Start(uint modifiers)
    {
        _modifiers = modifiers;
        if (_thread is not null) return;
        _thread = new Thread(ThreadProc) { IsBackground = true, Name = "LGforWin-Hotkeys" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
    }

    /// <summary>Re-registers the hotkeys with a new modifier bitmask (safe to call from any thread).</summary>
    public void UpdateModifiers(uint modifiers)
    {
        _modifiers = modifiers;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_APP_REREGISTER, IntPtr.Zero, IntPtr.Zero);
    }

    private void ThreadProc()
    {
        _threadId = GetCurrentThreadId();
        Register();
        _ready.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                var action = (int)msg.wParam == IdUp ? HotkeyAction.BrightnessUp : HotkeyAction.BrightnessDown;
                HotkeyPressed?.Invoke(action);
            }
            else if (msg.message == WM_APP_REREGISTER)
            {
                Register();
            }
        }

        UnregisterHotKey(IntPtr.Zero, IdUp);
        UnregisterHotKey(IntPtr.Zero, IdDown);
    }

    // (Re)registers the two hotkeys with the current modifiers. Never registers bare arrow keys
    // (modifiers == 0) — that would swallow every arrow press system-wide.
    private void Register()
    {
        UnregisterHotKey(IntPtr.Zero, IdUp);
        UnregisterHotKey(IntPtr.Zero, IdDown);
        if (_modifiers == 0) return;
        var mod = _modifiers | MOD_NOREPEAT;
        RegisterHotKey(IntPtr.Zero, IdUp, mod, VK_UP);
        RegisterHotKey(IntPtr.Zero, IdDown, mod, VK_DOWN);
    }

    public void Dispose()
    {
        if (_thread is null) return;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
        _thread = null;
        _ready.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }
}
