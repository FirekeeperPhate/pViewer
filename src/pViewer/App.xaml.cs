using System.Windows;
using System.Windows.Threading;
using pViewer.Services;

namespace pViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The UI is English-only: numbers and input parsing follow the same language.
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        var settings = SettingsStore.Load();
        ApplyTheme(settings.Theme);

        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0) _ = window.OpenAsync(e.Args[0]);
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

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Un errore imprevisto non deve chiudere il programma (e far perdere modifiche non salvate).
        pViewer.Views.MessageDialog.Show(Current.MainWindow, $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "pViewer", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
