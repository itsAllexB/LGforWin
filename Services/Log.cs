namespace LGforWin.Services;

/// <summary>Tiny append-only logger to %LOCALAPPDATA%\LGforWin\log.txt for diagnosing TV behaviour.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LGforWin", "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                // Keep the log small: reset if it grows past ~256 KB.
                if (File.Exists(Path) && new FileInfo(Path).Length > 256 * 1024)
                    File.WriteAllText(Path, "");
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
