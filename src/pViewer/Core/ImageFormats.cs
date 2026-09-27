namespace pViewer.Core;

/// <summary>Extensions recognized as images and as archives.</summary>
public static class ImageFormats
{
    // Decoded by WIC (modern/RAW formats need the Windows codecs),
    // with ImageSharp as a fallback for webp/tga/qoi/pbm when the codec is missing.
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff",
        ".ico", ".tbn", ".jxr", ".wdp", ".webp", ".heic", ".heif", ".avif", ".jxl",
        ".tga", ".qoi", ".pbm", ".pgm", ".ppm",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf",
    };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".cbz", ".rar", ".cbr", ".7z", ".cb7",
    };

    public static bool IsImage(string path) => Images.Contains(Path.GetExtension(path));

    public static bool IsArchive(string path) => Archives.Contains(Path.GetExtension(path));

    public static bool IsJpeg(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".tbn";

    public static string OpenDialogFilter
    {
        get
        {
            string all = string.Join(";", Images.Concat(Archives).Select(e => "*" + e));
            string img = string.Join(";", Images.Select(e => "*" + e));
            string arc = string.Join(";", Archives.Select(e => "*" + e));
            return $"All supported files|{all}|Images|{img}|Archives and comics|{arc}|All files|*.*";
        }
    }

    /// <summary>Formats that can be saved.</summary>
    public const string SaveDialogFilter =
        "JPEG|*.jpg;*.jpeg|PNG|*.png|WebP|*.webp|BMP|*.bmp|GIF|*.gif|TIFF|*.tif;*.tiff|JPEG XR|*.jxr";

    public static bool CanSave(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".png" or ".webp"
            or ".bmp" or ".gif" or ".tif" or ".tiff" or ".jxr" or ".wdp";
}
