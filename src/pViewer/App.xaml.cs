using System.Windows;
using System.Windows.Threading;
using pViewer.Services;

namespace pViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        MessageBox.Show(Current.MainWindow, $"Si è verificato un errore imprevisto:\n\n{e.Exception.Message}",
            "pViewer", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
