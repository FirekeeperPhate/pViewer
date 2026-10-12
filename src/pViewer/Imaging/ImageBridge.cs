using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pViewer.Imaging;

/// <summary>
/// 8-bit pixels in memory, four bytes each (blue, green, red, alpha; not premultiplied), top row
/// first: what the editing code and the decoders of pViewer itself work on.
/// </summary>
public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height) : this(new byte[ImageBridge.BufferSize(width, height)], width, height) { }

    public PixelBuffer(byte[] data, int width, int height)
    {
        if (width < 1 || height < 1 || data.LongLength < (long)width * height * 4)
            throw new ArgumentException("Pixel data smaller than the image.");
        (Data, Width, Height) = (data, width, height);
    }

    public byte[] Data { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
}

/// <summary>Conversions between BitmapSource (WPF/WIC) and <see cref="PixelBuffer"/>.</summary>
public static class ImageBridge
{
    public static PixelBuffer ToPixels(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize(w, h));
        bgra.CopyPixels(buffer, stride, 0);
        return new PixelBuffer(buffer, w, h);
    }

    public static BitmapSource ToBitmapSource(PixelBuffer pixels, double dpiX = 96, double dpiY = 96)
    {
        var bmp = BitmapSource.Create(pixels.Width, pixels.Height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels.Data, pixels.Stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>32-bit buffer size; beyond ~536 MP it would overflow an int (and a .NET array).</summary>
    internal static int BufferSize(int width, int height) => BufferSize((long)width * height * 4, width, height);

    private static int BufferSize(long size, int width, int height)
    {
        if (size > Array.MaxLength)
            throw new InvalidOperationException($"The image is too large to edit ({width} × {height}).");
        return (int)size;
    }

    /// <summary>
    /// Applies a transformation to the pixels of a BitmapSource (keeping its resolution). The
    /// transformation may change the buffer it is given and return it, or return a new one.
    /// </summary>
    public static BitmapSource Process(BitmapSource source, Func<PixelBuffer, PixelBuffer> process) =>
        ToBitmapSource(process(ToPixels(source)), source.DpiX, source.DpiY);

    /// <summary>
    /// Forces decoding and freezes: safe to pass between threads. Copies the pixels as they are
    /// (a CachedBitmap would turn 16-bit, CMYK or HDR images into 8-bit ones).
    /// </summary>
    public static BitmapSource Materialize(BitmapSource source)
    {
        long bytes = ((long)source.PixelWidth * source.Format.BitsPerPixel + 7) / 8 * source.PixelHeight;
        if (bytes <= Array.MaxLength) return WithDpi(source, source.DpiX, source.DpiY);
        // Too large for one array (a huge 16-bit scan): keep it editable at 8 bits per channel.
        var cached = new CachedBitmap(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }

    /// <summary>Frozen copy of the pixels with the given resolution (e.g. a 300 dpi scan after an edit).</summary>
    public static BitmapSource WithDpi(BitmapSource source, double dpiX, double dpiY)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        int stride = (int)(((long)w * source.Format.BitsPerPixel + 7) / 8);
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize((long)stride * h, w, h));
        source.CopyPixels(buffer, stride, 0);
        var bmp = BitmapSource.Create(w, h, dpiX, dpiY, source.Format, source.Palette, buffer, stride);
        bmp.Freeze();
        return bmp;
    }

    public static bool MayHaveAlpha(PixelFormat format) =>
        !(format == PixelFormats.Bgr24 || format == PixelFormats.Bgr32 || format == PixelFormats.Rgb24
          || format == PixelFormats.Gray8 || format == PixelFormats.Gray16 || format == PixelFormats.Gray32Float
          || format == PixelFormats.BlackWhite || format == PixelFormats.Gray2 || format == PixelFormats.Gray4
          || format == PixelFormats.Cmyk32 || format == PixelFormats.Bgr565 || format == PixelFormats.Bgr555
          || format == PixelFormats.Bgr101010 || format == PixelFormats.Rgb48 || format == PixelFormats.Rgb128Float);

    /// <summary>Memory held by the pixels (16-bit and float images take 2-4 times a 32-bit one).</summary>
    public static long EstimateBytes(BitmapSource bmp) =>
        (long)bmp.PixelWidth * bmp.PixelHeight * Math.Max(4, bmp.Format.BitsPerPixel / 8);
}
