using System.Windows;
using System.Windows.Threading;
using pViewer.Core;
using pViewer.Services;

namespace pViewer;

public partial class App : Application
{
    private readonly Task<AppSettings> _startupSettings;

    /// <summary>Tells the installer and the uninstaller that pViewer is running (AppMutex in pViewer.iss).</summary>
    private static Mutex? _runningMutex;

    public App()
    {
        StartupTrace.Mark("app created");
        _runningMutex = new Mutex(false, "pViewer.Running");
        // While WPF loads the theme (App.xaml), read the settings and start decoding the image
        // passed on the command line (double-click in Explorer) in the background.
        string[] args = Environment.GetCommandLineArgs();
        _startupSettings = Task.Run(() =>
        {
            var settings = SettingsStore.Load();
            if (args.Length > 1 && ImageFormats.IsImage(args[1]) && File.Exists(args[1]))
            {
                try { StartupPreload.Start(args[1], settings.AutoRotateExif); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { } // the normal open reports it
            }
            return settings;
        });
    }

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

        var settings = _startupSettings.GetAwaiter().GetResult();
        StartupTrace.Mark("settings");
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
        MainWindow = window;
        window.Show();
        StartupTrace.Mark("window shown");
        if (e.Args.Length > 0) _ = OpenFromCommandLineAsync(window, e.Args[0]);
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

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // An unexpected error must not close the app (and lose unsaved changes)…
        e.Handled = true;
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
