using System.Reflection;

namespace pViewer.Services;

/// <summary>
/// Unexpected errors, with their full stack, in %LocalAppData%\pViewer\errors.log: the dialog shows
/// only the message, and an error that happens while pViewer is closing is not shown at all. The
/// file is kept small (the older half goes to errors.old.log) and removed by the uninstaller.
/// </summary>
public static class ErrorLog
{
    private const long MaxSize = 256 * 1024;
    private static readonly Lock Gate = new();

    public static string FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pViewer", "errors.log");

    /// <summary>Appends the error; never throws (it runs inside the error handler).</summary>
    public static void Write(Exception error, string when)
    {
        try
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  pViewer {version}  {when}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxSize)
                    File.Move(FilePath, Path.ChangeExtension(FilePath, ".old.log"), overwrite: true);
                File.AppendAllText(FilePath, entry);
            }
        }
        catch (Exception)
        {
            // Read-only profile, disk full…: the error is still handled, only not written down.
        }
    }
}
