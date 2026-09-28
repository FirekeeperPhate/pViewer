using System.Diagnostics;

namespace pViewer;

/// <summary>
/// Startup timing for development: with PVIEWER_STARTUP_TRACE=&lt;file&gt; the milliseconds since the
/// process started are appended to that file at each step, the window opens off-screen without
/// taking the focus, the app exits once the first image has been drawn, and the settings (window
/// placement included) are not saved.
/// </summary>
internal static class StartupTrace
{
    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable("PVIEWER_STARTUP_TRACE") is { Length: > 0 } path ? path : null;
    private static bool _imageShown;

    public static bool Enabled => TracePath is not null;

    public static void Mark(string step)
    {
        if (TracePath is null) return;
        double ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        try { File.AppendAllText(TracePath, $"{step}\t{ms:0}\n"); } catch (IOException) { }
    }

    /// <summary>The first open finished without an image (error, no argument): exit anyway.</summary>
    public static void OpenFinished()
    {
        if (TracePath is null || _imageShown) return;
        Mark("no image");
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Called when pages are handed to the viewer: marks the first time and exits after drawing.</summary>
    public static void ImageShown()
    {
        if (TracePath is null || _imageShown) return;
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
