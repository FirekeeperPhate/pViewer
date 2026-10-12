using System.Diagnostics;

namespace pViewer;

/// <summary>
/// Startup timing for development: with PVIEWER_STARTUP_TRACE=&lt;file&gt; the milliseconds since the
/// process started are appended to that file at each step, the window opens off-screen without
/// taking the focus, the app exits once the first image has been drawn, and the settings (window
/// placement included) are not saved. A pViewer kept ready in the background that takes over a
/// start made this way times it from the start of the process that handed it over.
/// </summary>
internal static class StartupTrace
{
    private static string? _tracePath =
        Environment.GetEnvironmentVariable("PVIEWER_STARTUP_TRACE") is { Length: > 0 } path ? path : null;
    private static DateTime? _start;
    private static bool _imageShown;

    public static bool Enabled => _tracePath is not null;

    /// <summary>Not for the copy that waits in the background: it would time its own warm-up.</summary>
    public static void Disable() => _tracePath = null;

    /// <summary>Times a start handed over by another process, which began at <paramref name="startUtc"/>.</summary>
    public static void Begin(string path, DateTime startUtc)
    {
        _tracePath = path;
        _start = startUtc.ToLocalTime();
        _imageShown = false;
    }

    public static void Mark(string step)
    {
        if (_tracePath is null) return;
        double ms = (DateTime.Now - (_start ??= Process.GetCurrentProcess().StartTime)).TotalMilliseconds;
        try { File.AppendAllText(_tracePath, $"{step}\t{ms:0}\n"); } catch (IOException) { }
    }

    /// <summary>The first open finished without an image (error, no argument): exit anyway.</summary>
    public static void OpenFinished()
    {
        if (_tracePath is null || _imageShown) return;
        Mark("no image");
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Called when pages are handed to the viewer: marks the first time and exits after drawing.</summary>
    public static void ImageShown()
    {
        if (_tracePath is null || _imageShown) return;
        _imageShown = true;
        Mark("image set");
        var app = System.Windows.Application.Current;
        app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            Mark("image drawn");
            app.Shutdown();
        });
    }
}
