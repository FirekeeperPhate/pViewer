using System.Runtime.CompilerServices;
using pViewer.Services;

namespace pViewer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Before anything of WPF is loaded: if a pViewer is kept ready in the background, it takes
        // the file and shows its window, and this process is done in a fraction of the time.
        if (!Standby.IsStandbyStart(args) && Standby.TryHandOver(args)) return 0;
        return RunApp();
    }

    /// <summary>Apart, so that starting <see cref="Main"/> does not load the WPF assemblies.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
