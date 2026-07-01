using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace LGforWin.Services;

/// <summary>A connected display, identified by a stable EDID-based id and its friendly name.</summary>
public sealed class MonitorInfo
{
    /// <summary>Stable identifier (the monitor's device path, e.g. contains the EDID vendor/serial).
    /// Survives reboots, cable swaps and display-number reshuffles — persist THIS, not an index.</summary>
    public string Id { get; init; } = "";

    /// <summary>Friendly name from the EDID, e.g. "LG TV SSCR2". Falls back to the GDI device name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Work area (physical pixels, taskbar excluded) for placing the OSD.</summary>
    public RectInt32 WorkArea { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// Enumerates connected displays with their EDID friendly names (via the DISPLAYCONFIG APIs) so a
/// TV can be paired to the exact screen it drives, and resolves a stored id back to a work area.
/// </summary>
public static class MonitorService
{
    /// <summary>Lists the currently connected displays. Empty on failure (never throws).</summary>
    public static List<MonitorInfo> List()
    {
        try { return Entries().Select(e => e.Info).ToList(); }
        catch { return new List<MonitorInfo>(); }
    }

    /// <summary>Work area of the display with the given stable id, or null if it isn't connected.</summary>
    public static RectInt32? ResolveWorkArea(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return List().FirstOrDefault(m => m.Id == id)?.WorkArea;
    }

    /// <summary>Stable id of the display the mouse cursor is currently on, or null if unknown.</summary>
    public static string? MonitorIdAtCursor()
    {
        try
        {
            if (!GetCursorPos(out var pt)) return null;
            var hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
            if (!GetMonitorInfoW(hmon, ref mi)) return null;
            var device = mi.szDevice;
            return Entries().FirstOrDefault(e => string.Equals(e.Device, device, StringComparison.OrdinalIgnoreCase))?.Info.Id;
        }
        catch { return null; }
    }

    private sealed record Entry(string Device, MonitorInfo Info);

    private static List<Entry> Entries()
    {
        // 1. GDI device name ("\\.\DISPLAY1") -> work area, via the monitor enumeration.
        var workByDevice = new Dictionary<string, RectInt32>(StringComparer.OrdinalIgnoreCase);
        MonitorEnumProc cb = (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(h, ref mi))
            {
                var w = mi.rcWork;
                workByDevice[mi.szDevice] = new RectInt32(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

        // 2. DISPLAYCONFIG active paths -> source GDI name + target friendly name + stable device path.
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var numPath, out var numMode) != 0)
            return Fallback(workByDevice);

        var paths = new DISPLAYCONFIG_PATH_INFO[numPath];
        var modes = new DISPLAYCONFIG_MODE_INFO[numMode];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPath, paths, ref numMode, modes, IntPtr.Zero) != 0)
            return Fallback(workByDevice);

        var result = new List<Entry>();
        var seen = new HashSet<string>();
        for (var i = 0; i < numPath; i++)
        {
            var path = paths[i];

            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            src.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            src.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            src.header.adapterId = path.sourceInfo.adapterId;
            src.header.id = path.sourceInfo.id;
            if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;

            var gdi = src.viewGdiDeviceName;
            if (string.IsNullOrEmpty(gdi) || !workByDevice.TryGetValue(gdi, out var work)) continue;

            var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            tgt.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            tgt.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            tgt.header.adapterId = path.targetInfo.adapterId;
            tgt.header.id = path.targetInfo.id;

            string id, name;
            if (DisplayConfigGetDeviceInfo(ref tgt) == 0)
            {
                id = string.IsNullOrEmpty(tgt.monitorDevicePath) ? gdi : tgt.monitorDevicePath;
                name = string.IsNullOrWhiteSpace(tgt.monitorFriendlyDeviceName) ? gdi : tgt.monitorFriendlyDeviceName;
            }
            else
            {
                id = gdi;
                name = gdi;
            }

            if (!seen.Add(id)) continue; // skip clone-mode duplicates
            result.Add(new Entry(gdi, new MonitorInfo { Id = id, Name = name, WorkArea = work }));
        }

        return result.Count > 0 ? result : Fallback(workByDevice);
    }

    // If DISPLAYCONFIG is unavailable, still expose the displays keyed by their GDI device name.
    private static List<Entry> Fallback(Dictionary<string, RectInt32> workByDevice) =>
        workByDevice.Select(kv => new Entry(kv.Key, new MonitorInfo { Id = kv.Key, Name = kv.Key, WorkArea = kv.Value })).ToList();

    // ----- Win32 interop -----

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx;
        public uint outputTechnology; public uint rotation; public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // We never read the mode info; just size the buffer correctly (union is 64 bytes).
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type; public uint size; public LUID adapterId; public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
}
