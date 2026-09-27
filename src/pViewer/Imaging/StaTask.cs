using System.Windows.Threading;

namespace pViewer.Imaging;

/// <summary>
/// Esegue lavoro in background su un thread STA: DrawingVisual e RenderTargetBitmap
/// non funzionano sui thread MTA del thread pool.
/// </summary>
public static class StaTask
{
    public static Task<T> Run<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
            finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
