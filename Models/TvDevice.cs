namespace LGforWin.Models;

/// <summary>
/// Persisted description of a single LG webOS TV plus its last-known state.
/// Stored as JSON in %LOCALAPPDATA%\LGforWin\devices.json.
/// </summary>
public sealed class TvDevice
{
    /// <summary>Friendly name shown in the UI (e.g. "Living Room C3").</summary>
    public string Name { get; set; } = "LG TV";

    /// <summary>LAN host/IP of the TV, e.g. "192.168.1.42".</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// Pairing key returned by the TV on first accept. Reused on every later
    /// connection so the on-screen prompt never appears again. Empty until paired.
    /// </summary>
    public string ClientKey { get; set; } = "";

    /// <summary>Last OLED Light (backlight) value 0-100 we set, for UI restore.</summary>
    public int LastBacklight { get; set; } = 80;

    /// <summary>
    /// Stable id (<see cref="LGforWin.Services.MonitorInfo.Id"/>) of the Windows display this TV
    /// drives, so the OSD can appear on that screen when the "TV's screen" mode is chosen. Empty
    /// until the user pairs it on the Home page.
    /// </summary>
    public string PairedScreenId { get; set; } = "";

    /// <summary>
    /// True once a secure wss://:3001 connection succeeded (2022+ models). Remembered so
    /// reconnects try the working transport first instead of timing out on ws://:3000.
    /// </summary>
    public bool Secure { get; set; }

    /// <summary>
    /// The TV's MAC address ("AA:BB:CC:DD:EE:FF"), learned via ARP the first time we connect
    /// and needed to wake the TV over the network (Wake-on-LAN) once it's off. Empty until learned.
    /// </summary>
    public string MacAddress { get; set; } = "";
}
