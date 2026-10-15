using System.Windows;
using System.Windows.Threading;
using pViewer.Core;
using pViewer.Services;

namespace pViewer;

public partial class App : Application
{
    /// <summary>null for the copy that waits in the background: it reads the settings when it is given a file.</summary>
    private readonly Task<AppSettings>? _startupSettings;

    /// <summary>Tells the installer and the uninstaller that pViewer is running (AppMutex in pViewer.iss).</summary>
    private static Mutex? _runningMutex;

    public App()
    {
        if (Standby.IsStandbyStart(Environment.GetCommandLineArgs()[1..]))
        {
            _standby = true;
            StartupTrace.Disable();
            return;
        }
        StartupTrace.Mark("app created");
        _runningMutex = new Mutex(false, "pViewer.Running");
        // While WPF loads the theme (App.xaml), read the settings and start decoding the image
        // passed on the command line (double-click in Explorer) in the background.
        _startupSettings = LoadSettingsAndPreload(Environment.GetCommandLineArgs()[1..]);
    }

    private static Task<AppSettings> LoadSettingsAndPreload(string[] args) => Task.Run(() =>
    {
        var settings = SettingsStore.Load();
        if (args.Length > 0 && ImageFormats.IsImage(args[0]) && File.Exists(args[0]))
        {
            try { StartupPreload.Start(args[0], settings.AutoRotateExif); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { } // the normal open reports it
        }
        return settings;
    });

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTrace.Mark("startup");
        base.OnStartup(e);
        // The UI is English-only: numbers and input parsing follow the same language.
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        if (_standby || _startupSettings is null)
        {
            _ = RunStandbyAsync();
            return;
        }
        var settings = _startupSettings.GetAwaiter().GetResult();
        StartupTrace.Mark("settings");
        ShowMainWindow(settings, e.Args);
    }

    /// <summary>The window of a normal start, or of the copy kept ready once it is given a file.</summary>
    private void ShowMainWindow(AppSettings settings, string[] args)
    {
        ApplyTheme(settings.Theme);
        StartupTrace.Mark("theme");

        if (StartupTrace.Enabled) settings.Window = null; // off-screen, see below
        var window = new MainWindow(settings);
        StartupTrace.Mark("window created");
        if (StartupTrace.Enabled)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.ShowActivated = false;
            window.ContentRendered += (_, _) => StartupTrace.Mark("window drawn");
        }
        else
        {
            // Once the first image is up, and without competing with it: the copy that makes the
            // next start faster (see Standby).
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                Standby.EnsureRunning(settings);
            };
        }
        window.Closed += (_, _) => _windowClosed = true;
        MainWindow = window;
        window.Show();
        StartupTrace.Mark("window shown");
        if (args.Length > 0) _ = OpenFromCommandLineAsync(window, args[0]);
        else StartupTrace.OpenFinished();
    }

    private static async Task OpenFromCommandLineAsync(MainWindow window, string path)
    {
        try { await window.OpenAsync(path); }
        finally
        {
            StartupPreload.Discard();
            StartupTrace.OpenFinished();
        } // not taken (e.g. the file is hidden from the list): free it
    }

    public static void ApplyTheme(AppTheme theme)
    {
        Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.System => ThemeMode.System,
            _ => ThemeMode.Dark,
        };
    }

    private static bool _showingError;

    /// <summary>The main window has been closed: what is still running is the end of the application.</summary>
    private static bool _windowClosed;

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // An unexpected error must not close the app (and lose unsaved changes)…
        e.Handled = true;
        // After the window was closed the application is only ending: nothing to tell, nobody to tell it to.
        if (_windowClosed || Current is null) return;
        // The copy that waits in the background has no window to show an error in: it just ends.
        if (Current is App { _standby: true })
        {
            Current.Shutdown(1);
            return;
        }
        // …and an error that repeats on every layout pass must not stack dialogs.
        if (_showingError) return;
        _showingError = true;
        try
        {
            var main = Current.MainWindow;
            bool started = main is { IsLoaded: true };
            pViewer.Views.MessageDialog.Show(main, $"An unexpected error occurred:\n\n{e.Exception.Message}",
                "pViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            // Failed while starting: there is no usable window, so exit instead of lingering.
            if (!started) Current.Shutdown(1);
        }
        finally
        {
            _showingError = false;
        }
    }
}
