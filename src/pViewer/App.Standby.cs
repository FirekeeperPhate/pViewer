using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using pViewer.Services;

namespace pViewer;

/// <summary>
/// The copy kept ready in the background ("pViewer.exe --standby", see <see cref="Standby"/>): it
/// starts everything a window needs, shows nothing, and waits for a pViewer started by the user to
/// hand it a file.
/// </summary>
public partial class App
{
    /// <summary>True until this copy is given a file and becomes an ordinary pViewer.</summary>
    private bool _standby;
    private Mutex? _standbyMutex;
    private EventWaitHandle? _standbyExit;
    private RegisteredWaitHandle? _standbyExitWait;

    private async Task RunStandbyAsync()
    {
        // No window yet, and the one used to warm up must not end the process when it closes.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _standbyMutex = new Mutex(false, Standby.MutexName, out bool first);
        var settings = SettingsStore.Load();
        if (!first || !settings.KeepReady || !Standby.Allowed)
        {
            Shutdown();
            return;
        }
        // The installer, the uninstaller and the option being turned off stop it through this.
        _standbyExit = new EventWaitHandle(false, EventResetMode.ManualReset, Standby.ExitName);
        _standbyExitWait = ThreadPool.RegisterWaitForSingleObject(_standbyExit,
            (_, _) => Dispatcher.BeginInvoke(() => { if (_standby) Shutdown(); }), null, Timeout.Infinite, executeOnlyOnce: true);

        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            // The warm-up must not slow down the pViewer that has just opened in front of the user.
            try { process.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            if (!await WarmUpAsync(settings))
            {
                Shutdown();
                return;
            }
            try { process.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal; } catch (Exception) { }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        // It may wait for hours: give the memory back to Windows (about 120 MB become 20), which
        // returns the pages as they are touched again when a file arrives (some 15 ms, measured).
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
            SetProcessWorkingSetSize(process.Handle, -1, -1);

        while (_standby)
        {
            StandbyRequest? request;
            var pipe = new NamedPipeServerStream(Standby.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                // The sign that a start can be handed over exists only while this waits.
                using (new EventWaitHandle(false, EventResetMode.ManualReset, Standby.ReadyName))
                    await pipe.WaitForConnectionAsync();
                var serving = Task.Run(() => Standby.Serve(pipe));
                // A caller that connects and says nothing must not hold this copy for ever: closing the
                // pipe ends the wait. The answer still counts (it may have been given at that very moment).
                if (await Task.WhenAny(serving, Task.Delay(3000)) != serving) pipe.Dispose();
                request = await serving;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                request = null;
            }
            finally
            {
                pipe.Dispose();
            }
            if (request is null) continue;
            if (_standby) BecomeOrdinary(request);
            return;
        }
    }

    /// <summary>
    /// Opens a window off-screen on a small picture and closes it again: the code of a start is
    /// compiled, the libraries are loaded and the drawing engine is running before the first real use.
    /// </summary>
    /// <returns>false if it got stuck (an error dialog nobody can see): this copy then gives up.</returns>
    private async Task<bool> WarmUpAsync(AppSettings settings)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"pViewer-warmup-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(folder);
            string picture = Path.Combine(folder, "warm.jpg");
            var pixels = new byte[64 * 64 * 3];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgr24, null, pixels, 64 * 3)));
            using (var stream = File.Create(picture)) encoder.Save(stream);

            ApplyTheme(settings.Theme);
            settings.Window = null; // never where the user left the window, nor maximized
            var window = new MainWindow(settings)
            {
                WarmUp = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            window.Show();
            var opening = window.OpenAsync(picture);
            if (await Task.WhenAny(opening, Task.Delay(TimeSpan.FromSeconds(20))) != opening) return false;
            // Drawn, and the menus that are built once the window is idle are built.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.Close();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true; // a warm-up that fails only means a slower first window
        }
        finally
        {
            MainWindow = null;
            try { Directory.Delete(folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A pViewer started by the user handed over its command line: from here on this is that pViewer.</summary>
    private void BecomeOrdinary(StandbyRequest request)
    {
        _standby = false;
        _standbyExitWait?.Unregister(null);
        _standbyExit?.Dispose();
        _standbyMutex?.Dispose(); // the next copy to wait can start
        _standbyMutex = null;

        // Relative paths on the command line, and the folder a dialog opens in, are the caller's.
        try { Environment.CurrentDirectory = request.WorkingDirectory; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException) { }
        if (request.TracePath.Length > 0) StartupTrace.Begin(request.TracePath, new DateTime(request.StartTicksUtc, DateTimeKind.Utc));
        StartupTrace.Mark("handed over");
        _runningMutex = new Mutex(false, "pViewer.Running");
        // The settings as they are now, not as they were when this copy started waiting.
        var settings = LoadSettingsAndPreload(request.Args).GetAwaiter().GetResult();
        StartupTrace.Mark("settings");
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        ShowMainWindow(settings, request.Args);
        if (!StartupTrace.Enabled && MainWindow is { } window)
        {
            // Shown by a process that was in the background: ask for the front (the caller allowed it).
            window.Activate();
            SetForegroundWindow(new WindowInteropHelper(window).Handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
