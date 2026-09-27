using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace pViewer.Imaging;

/// <summary>A decoded image, ready to display.</summary>
public sealed class LoadedImage
{
    public required BitmapSource Bitmap { get; init; }

    /// <summary>Original metadata (JPEG only), kept when saving.</summary>
    public BitmapMetadata? JpegMetadata { get; init; }

    /// <summary>Frames of animated GIF, WebP or PNG (null for static images).</summary>
    public ImageAnimation? Animation { get; init; }

    /// <summary>
    /// Decoded with EXIF auto-rotation: the pixels are upright, so a saved JPEG must get
    /// Orientation = 1. Otherwise the original tag is kept.
    /// </summary>
    public bool AutoOriented { get; init; }

    /// <summary>Pages in the file (multi-page TIFF): only the first one is shown and edited.</summary>
    public int PageCount { get; init; } = 1;

    public string FormatName { get; init; } = "";
    public long FileSize { get; init; }
    public int PixelWidth => Bitmap.PixelWidth;
    public int PixelHeight => Bitmap.PixelHeight;
}

/// <summary>Already composited (full size) frames with the duration of each one.</summary>
public sealed record ImageAnimation(IReadOnlyList<BitmapSource> Frames, IReadOnlyList<TimeSpan> Delays);

public sealed class ImageDecodeException(string message, Exception? inner) : Exception(message, inner);

public static class ImageDecoder
{
    /// <summary>Beyond this much memory only the first frame of the animation is shown.</summary>
    public const long MaxAnimationBytes = 768L * 1024 * 1024;

    public static LoadedImage Decode(byte[] data, string name, bool autoOrient)
    {
        if (MayBeAnimated(data))
        {
            try
            {
                var animated = TryDecodeAnimation(data);
                if (animated is not null) return animated;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Animated file ImageSharp cannot read: try it as a static image.
            }
        }

        Exception? firstError = null;
        foreach (var options in new[] { BitmapCreateOptions.PreservePixelFormat,
                                        BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile })
        {
            try { return DecodeWic(data, autoOrient, options); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { firstError ??= ex; }
        }

        try { return DecodeImageSharp(data, autoOrient); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { firstError ??= ex; }

        throw new ImageDecodeException(FriendlyError(name), firstError);
    }

    private static string FriendlyError(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        string hint = ext switch
        {
            ".heic" or ".heif" => "Install «HEIF Image Extensions» from the Microsoft Store.",
            ".avif" => "Install «AV1 Video Extension» from the Microsoft Store.",
            ".jxl" => "Install «JPEG XL Image Extension» from the Microsoft Store.",
            ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".raf"
                => "Install «Raw Image Extension» from the Microsoft Store.",
            _ => "The file may be damaged or in an unsupported format.",
        };
        return $"Cannot open «{Path.GetFileName(name)}».\n{hint}";
    }

    /// <summary>Recognizes from the first bytes the formats that can hold animations (GIF, WebP, PNG/APNG).</summary>
    private static bool MayBeAnimated(byte[] d) =>
        (d.Length > 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8')
        || (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
        || (d.Length > 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G');

    /// <returns>null if the file has a single frame or the animation would take too much memory.</returns>
    private static LoadedImage? TryDecodeAnimation(byte[] data)
    {
        var info = ISImage.Identify(data);
        int frameCount = info.FrameMetadataCollection.Count;
        if (frameCount < 2 || (long)info.Width * info.Height * 4 * frameCount > MaxAnimationBytes) return null;

        using var img = ISImage.Load<Bgra32>(data);
        var format = img.Metadata.DecodedImageFormat;
        var frames = new List<BitmapSource>(img.Frames.Count);
        var delays = new List<TimeSpan>(img.Frames.Count);
        var buffer = new byte[img.Width * img.Height * 4];
        foreach (var frame in img.Frames)
        {
            frame.CopyPixelDataTo(buffer);
            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, buffer, img.Width * 4);
            bmp.Freeze();
            frames.Add(bmp);
            delays.Add(FrameDelay(frame, format));
        }
        return new LoadedImage
        {
            Bitmap = frames[0],
            Animation = new ImageAnimation(frames, delays),
            FormatName = format?.Name ?? "",
            FileSize = data.LongLength,
        };
    }

    private static TimeSpan FrameDelay(SixLabors.ImageSharp.ImageFrame frame, SixLabors.ImageSharp.Formats.IImageFormat? format)
    {
        double ms = format switch
        {
            SixLabors.ImageSharp.Formats.Gif.GifFormat => frame.Metadata.GetGifMetadata().FrameDelay * 10.0,
            SixLabors.ImageSharp.Formats.Webp.WebpFormat => frame.Metadata.GetWebpMetadata().FrameDelay,
            SixLabors.ImageSharp.Formats.Png.PngFormat => RationalMs(frame.Metadata.GetPngMetadata().FrameDelay),
            _ => 100,
        };
        // Like browsers: near-zero durations become 100 ms (many GIFs use 0 or 1 hundredth).
        return TimeSpan.FromMilliseconds(ms < 20 ? 100 : ms);
    }

    private static double RationalMs(SixLabors.ImageSharp.Rational r) =>
        r.Denominator == 0 ? 0 : r.Numerator * 1000.0 / r.Denominator;

    private static LoadedImage DecodeWic(byte[] data, bool autoOrient, BitmapCreateOptions options)
    {
        // Do not close the stream: WIC may read the metadata lazily.
        var ms = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(ms, options, BitmapCacheOption.OnLoad);
        // Icons hold several sizes of the same picture: show the largest. Other multi-frame files
        // (multi-page TIFF) show their first page.
        bool icon = decoder is IconBitmapDecoder;
        BitmapFrame frame = icon && decoder.Frames.Count > 1
            ? decoder.Frames.MaxBy(f => (long)f.PixelWidth * f.PixelHeight)!
            : decoder.Frames[0];

        ushort orientation = autoOrient ? ReadOrientation(frame) : (ushort)1;
        BitmapSource bitmap = orientation > 1 ? ApplyOrientation(frame, orientation) : frame;
        if (!bitmap.IsFrozen)
        {
            if (bitmap.CanFreeze) bitmap.Freeze();
            else bitmap = ImageBridge.Materialize(bitmap);
        }

        BitmapMetadata? jpegMeta = null;
        if (decoder is JpegBitmapDecoder && frame.Metadata is BitmapMetadata md)
        {
            try
            {
                jpegMeta = md.Clone();
                jpegMeta.Freeze();
            }
            catch (Exception) { jpegMeta = null; }
        }

        return new LoadedImage
        {
            Bitmap = bitmap,
            JpegMetadata = jpegMeta,
            FormatName = FormatName(decoder),
            FileSize = data.LongLength,
            AutoOriented = autoOrient,
            PageCount = icon ? 1 : decoder.Frames.Count,
        };
    }

    private static string FormatName(BitmapDecoder decoder) => decoder switch
    {
        JpegBitmapDecoder => "JPEG",
        PngBitmapDecoder => "PNG",
        GifBitmapDecoder => "GIF",
        BmpBitmapDecoder => "BMP",
        TiffBitmapDecoder => "TIFF",
        IconBitmapDecoder => "ICO",
        WmpBitmapDecoder => "JPEG XR",
        _ => SafeCodecName(decoder),
    };

    private static string SafeCodecName(BitmapDecoder decoder)
    {
        try { return decoder.CodecInfo?.FriendlyName?.Replace(" Decoder", "") ?? ""; }
        catch (Exception) { return ""; }
    }

    private static LoadedImage DecodeImageSharp(byte[] data, bool autoOrient)
    {
        using var img = ISImage.Load<Bgra32>(data);
        if (autoOrient) img.Mutate(x => x.AutoOrient());
        return new LoadedImage
        {
            Bitmap = ImageBridge.ToBitmapSource(img),
            FormatName = img.Metadata.DecodedImageFormat?.Name ?? "",
            FileSize = data.LongLength,
            AutoOriented = autoOrient,
        };
    }

    /// <summary>
    /// Embedded EXIF thumbnail (camera JPEGs): it reads in a few milliseconds
    /// and is shown while the full image is being decoded.
    /// </summary>
    /// <returns>The thumbnail and the size of the full image (already rotated upright).</returns>
    public static (BitmapSource Thumbnail, int FullWidth, int FullHeight)? TryDecodeEmbeddedPreview(byte[] data, bool autoOrient)
    {
        try
        {
            var ms = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder is not JpegBitmapDecoder) return null;
            var frame = decoder.Frames[0];
            var thumb = frame.Thumbnail;
            if (thumb is null) return null;
            ushort orientation = autoOrient ? ReadOrientation(frame) : (ushort)1;
            var preview = orientation > 1 ? ApplyOrientation(thumb, orientation) : ImageBridge.Materialize(thumb);
            bool swap = orientation >= 5;
            return (preview, swap ? frame.PixelHeight : frame.PixelWidth, swap ? frame.PixelWidth : frame.PixelHeight);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public static ushort ReadOrientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata md) return 1;
        foreach (var query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (md.GetQuery(query) is ushort v && v is >= 1 and <= 8) return v;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or COMException) { }
        }
        return 1;
    }

    /// <summary>Rotates the image upright according to the EXIF Orientation tag (1-8).</summary>
    public static BitmapSource ApplyOrientation(BitmapSource source, ushort orientation)
    {
        var group = new TransformGroup();
        switch (orientation)
        {
            case 2: group.Children.Add(new ScaleTransform(-1, 1)); break;
            case 3: group.Children.Add(new RotateTransform(180)); break;
            case 4: group.Children.Add(new ScaleTransform(1, -1)); break;
            case 5: group.Children.Add(new RotateTransform(90)); group.Children.Add(new ScaleTransform(-1, 1)); break;
            case 6: group.Children.Add(new RotateTransform(90)); break;
            case 7: group.Children.Add(new RotateTransform(270)); group.Children.Add(new ScaleTransform(-1, 1)); break;
            case 8: group.Children.Add(new RotateTransform(270)); break;
            default: return source;
        }
        return ImageBridge.Materialize(new TransformedBitmap(source, group));
    }
}
