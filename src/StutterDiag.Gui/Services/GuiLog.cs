using System.IO;
using System.Text;

namespace StutterDiag.Gui.Services;

/// <summary>
/// Deliberately tiny append-only logger for the GUI process. Writes to
/// <c>%LOCALAPPDATA%\StutterDiag\gui.log</c>. Never throws.
/// </summary>
public static class GuiLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string FilePath => _path ??= ResolvePath();

    private static string ResolvePath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StutterDiag");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "gui.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "stutterdiag-gui.log");
        }
    }

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                // Size-cap: if the file grows past ~1 MB, start fresh (this is a convenience log).
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.WriteAllText(FilePath, string.Empty);
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // A logger must never take the app down.
        }
    }
}
