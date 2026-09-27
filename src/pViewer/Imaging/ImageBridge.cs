using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp.PixelFormats;
using ISImage = SixLabors.ImageSharp.Image;

namespace pViewer.Imaging;

/// <summary>Conversions between BitmapSource (WPF/WIC) and Image&lt;Bgra32&gt; (ImageSharp).</summary>
public static class ImageBridge
{
    public static SixLabors.ImageSharp.Image<Bgra32> ToImageSharp(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize(w, h));
        bgra.CopyPixels(buffer, stride, 0);
        return ISImage.LoadPixelData<Bgra32>(buffer, w, h);
    }

    public static BitmapSource ToBitmapSource(SixLabors.ImageSharp.Image<Bgra32> image, double dpiX = 96, double dpiY = 96)
    {
        int w = image.Width, h = image.Height;
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize(w, h));
        image.CopyPixelDataTo(buffer);
        var bmp = BitmapSource.Create(w, h, dpiX, dpiY, PixelFormats.Bgra32, null, buffer, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>32-bit buffer size; beyond ~536 MP it would overflow an int (and a .NET array).</summary>
    private static int BufferSize(int width, int height) => BufferSize((long)width * height * 4, width, height);

    private static int BufferSize(long size, int width, int height)
    {
        if (size > Array.MaxLength)
            throw new InvalidOperationException($"The image is too large to edit ({width} × {height}).");
        return (int)size;
    }

    /// <summary>Applies an ImageSharp transformation to a BitmapSource (keeping its resolution).</summary>
    public static BitmapSource Process(BitmapSource source, Action<SixLabors.ImageSharp.Image<Bgra32>> process)
    {
        using var img = ToImageSharp(source);
        process(img);
        return ToBitmapSource(img, source.DpiX, source.DpiY);
    }

    /// <summary>
    /// Forces decoding and freezes: safe to pass between threads. Copies the pixels as they are
    /// (a CachedBitmap would turn 16-bit, CMYK or HDR images into 8-bit ones).
    /// </summary>
    public static BitmapSource Materialize(BitmapSource source) => WithDpi(source, source.DpiX, source.DpiY);

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

    public static long EstimateBytes(BitmapSource bmp) => (long)bmp.PixelWidth * bmp.PixelHeight * 4;
}
