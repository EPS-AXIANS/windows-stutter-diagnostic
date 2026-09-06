using System.Diagnostics;
using System.IO;

namespace StutterDiag.Gui.Services;

/// <summary>Best-effort helpers to open a file or folder with the shell. Never throws.</summary>
public static class Shell
{
    public static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GuiLog.Error($"Shell open failed for '{path}'", ex);
        }
    }

    public static void OpenContainingFolder(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            if (File.Exists(filePath))
            {
                // Open Explorer with the file selected.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GuiLog.Error($"Open containing folder failed for '{filePath}'", ex);
        }
    }

    /// <summary>%LOCALAPPDATA%\StutterDiag\reports, created on demand.</summary>
    public static string DefaultReportDirectory()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StutterDiag", "reports");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }
}
