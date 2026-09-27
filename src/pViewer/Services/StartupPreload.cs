using pViewer.Imaging;

namespace pViewer.Services;

/// <summary>
/// The image passed on the command line (double-click in Explorer) is read and decoded in the
/// background while WPF builds the window, instead of after it: the page cache then takes the
/// finished result for that file.
/// </summary>
internal static class StartupPreload
{
    private static string? _path;
    private static bool _autoOrient;
    private static Task<byte[]>? _bytes;
    private static Task<LoadedImage>? _image;
    private static readonly object Lock = new();

    public static void Start(string path, bool autoOrient)
    {
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        var bytes = Task.Run(() => File.ReadAllBytesAsync(full));
        var image = Task.Run(async () => ImageDecoder.Decode(await bytes.ConfigureAwait(false), name, autoOrient));
        // Never taken (e.g. the file vanished): its errors must not stay unobserved.
        _ = image.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        lock (Lock) (_path, _autoOrient, _bytes, _image) = (full, autoOrient, bytes, image);
    }

    /// <summary>Hands over the preloaded file once, if it is the one asked for with the same options.</summary>
    public static bool TryTake(string? filePath, bool autoOrient, out Task<byte[]> bytes, out Task<LoadedImage> image)
    {
        lock (Lock)
        {
            if (_path is not null && _bytes is not null && _image is not null && filePath is not null && autoOrient == _autoOrient
                && string.Equals(Path.GetFullPath(filePath), _path, StringComparison.OrdinalIgnoreCase))
            {
                (bytes, image) = (_bytes, _image);
                Discard();
                return true;
            }
        }
        bytes = null!;
        image = null!;
        return false;
    }

    /// <summary>Releases a preload nobody took (the first image has been shown by now).</summary>
    public static void Discard()
    {
        lock (Lock) (_path, _bytes, _image) = (null, null, null);
    }
}
