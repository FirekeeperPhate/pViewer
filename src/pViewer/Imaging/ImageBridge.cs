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
        var buffer = GC.AllocateUninitializedArray<byte>(stride * h);
        bgra.CopyPixels(buffer, stride, 0);
        return ISImage.LoadPixelData<Bgra32>(buffer, w, h);
    }

    public static BitmapSource ToBitmapSource(SixLabors.ImageSharp.Image<Bgra32> image)
    {
        int w = image.Width, h = image.Height;
        var buffer = GC.AllocateUninitializedArray<byte>(w * h * 4);
        image.CopyPixelDataTo(buffer);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buffer, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Applies an ImageSharp transformation to a BitmapSource.</summary>
    public static BitmapSource Process(BitmapSource source, Action<SixLabors.ImageSharp.Image<Bgra32>> process)
    {
        using var img = ToImageSharp(source);
        process(img);
        return ToBitmapSource(img);
    }

    /// <summary>Forces decoding and freezes: safe to pass between threads.</summary>
    public static BitmapSource Materialize(BitmapSource source)
    {
        var cached = new CachedBitmap(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }

    public static bool MayHaveAlpha(PixelFormat format) =>
        !(format == PixelFormats.Bgr24 || format == PixelFormats.Bgr32 || format == PixelFormats.Rgb24
          || format == PixelFormats.Gray8 || format == PixelFormats.Gray16 || format == PixelFormats.Gray32Float
          || format == PixelFormats.BlackWhite || format == PixelFormats.Gray2 || format == PixelFormats.Gray4
          || format == PixelFormats.Cmyk32 || format == PixelFormats.Bgr565 || format == PixelFormats.Bgr555
          || format == PixelFormats.Bgr101010 || format == PixelFormats.Rgb48 || format == PixelFormats.Rgb128Float);

    public static long EstimateBytes(BitmapSource bmp) => (long)bmp.PixelWidth * bmp.PixelHeight * 4;
}
