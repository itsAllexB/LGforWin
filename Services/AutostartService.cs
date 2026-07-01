using Microsoft.Win32;

namespace LGforWin.Services;

/// <summary>Opt-in "launch at sign-in" via the per-user HKCU Run key.</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LGforWin";

    /// <summary>Command-line argument the autostart entry passes, so the app can tell an
    /// at-sign-in launch from a manual one (manual launches always open the window).</summary>
    public const string AutostartArg = "--autostart";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, $"\"{exe}\" {AutostartArg}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* registry access denied — non-fatal */ }
    }
}
