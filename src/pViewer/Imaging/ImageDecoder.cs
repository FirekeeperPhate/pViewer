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

    /// <summary>
    /// Embedded color profile (ICC), only when the pixels are still in the file's color space
    /// (16-bit, float, gray, CMYK): a saved file must carry it again. 8-bit RGB pixels are
    /// converted to sRGB while decoding, so for them this is null.
    /// </summary>
    public IReadOnlyList<ColorContext>? ColorContexts { get; init; }

    /// <summary>Frames of animated GIF, WebP or PNG (null for static images).</summary>
    public ImageAnimation? Animation { get; init; }

    /// <summary>
    /// Decoded with EXIF auto-rotation: the pixels are upright, so a saved JPEG must get
    /// Orientation = 1. Otherwise the original tag is kept.
    /// </summary>
    public bool AutoOriented { get; init; }

    /// <summary>Pages in the file (multi-page TIFF): only the first one is shown and edited.</summary>
    public int PageCount { get; init; } = 1;

    /// <summary>
    /// Frames in the file. More than one with no <see cref="Animation"/> means an animation shown
    /// still (too large, or unreadable as an animation): it must not be overwritten either.
    /// </summary>
    public int FrameCount { get; internal set; } = 1;

    public bool IsAnimated => Animation is not null || FrameCount > 1;

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
    /// <summary>The standard sRGB profile (WPF's default for RGB pixel formats).</summary>
    public static ColorContext Srgb => new(PixelFormats.Bgra32); // a new one each time: used from several threads

    /// <summary>Beyond this much memory only the first frame of the animation is shown.</summary>
    public const long MaxAnimationBytes = 768L * 1024 * 1024;

    /// <summary>From this many frames on the file is shown still: each frame also costs objects and UI work.</summary>
    public const int MaxAnimationFrames = 5000;

    /// <summary>Largest image decoded (the ImageSharp fallback would otherwise allocate whatever a header claims).</summary>
    public const long MaxPixels = 400_000_000;

    public static LoadedImage Decode(byte[] data, string name, bool autoOrient)
    {
        // WIC inflates a PNG's color profile even when told to ignore it: a crafted 300 KB profile
        // that expands to hundreds of MB would take half a minute to decode.
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && PngProfileTooLarge(data))
            throw new ImageDecodeException(FriendlyError(name), new InvalidDataException("Oversized color profile."));

        int frameCount = 1;
        if (MayBeAnimated(data))
        {
            try
            {
                var animated = TryDecodeAnimation(data, out frameCount);
                if (animated is not null) return animated;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Animated file ImageSharp cannot read: try it as a static image.
            }
        }

        Exception? firstError = null;
        if (frameCount > 1)
        {
            // An animation not played (too many or too large frames): its first frame only. WIC would
            // load every frame first (half a minute for a million-frame GIF).
            try
            {
                var still = DecodeImageSharp(data, autoOrient, firstFrameOnly: true);
                still.FrameCount = frameCount;
                return still;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { firstError = ex; }
        }
        foreach (var options in new[] { BitmapCreateOptions.PreservePixelFormat,
                                        BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile })
        {
            try
            {
                var still = DecodeWic(data, autoOrient, options);
                still.FrameCount = Math.Max(still.FrameCount, frameCount);
                return still;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { firstError ??= ex; }
        }

        try
        {
            var still = DecodeImageSharp(data, autoOrient);
            still.FrameCount = Math.Max(still.FrameCount, frameCount);
            return still;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { firstError ??= ex; }

        throw new ImageDecodeException(FriendlyError(name), firstError);
    }

    private static string FriendlyError(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        string hint = ext switch
        {
            ".heic" or ".heif" => "Install “HEIF Image Extensions” from the Microsoft Store.",
            ".avif" => "Install “AV1 Video Extension” from the Microsoft Store.",
            ".jxl" => "Install “JPEG XL Image Extension” from the Microsoft Store.",
            ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".raf"
                => "Install “Raw Image Extension” from the Microsoft Store.",
            _ => "The file may be damaged or in an unsupported format.",
        };
        return $"Cannot open “{Path.GetFileName(name)}”.\n{hint}";
    }

    /// <summary>Recognizes from the first bytes the formats that can hold animations (GIF, WebP, PNG/APNG).</summary>
    private static bool MayBeAnimated(byte[] d) =>
        (d.Length > 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8')
        || (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
        || (d.Length > 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G');

    /// <returns>null if the file has a single frame or the animation would take too much memory.</returns>
    /// <param name="frameCount">Frames in the file, also when null is returned (animation too large).</param>
    private static LoadedImage? TryDecodeAnimation(byte[] data, out int frameCount)
    {
        frameCount = 1;
        int width, height;
        if (data[0] == 0x89)
        {
            // PNG: ImageSharp 3.1 Identify rejects valid APNGs, so the chunks are read directly.
            if (!TryReadApngInfo(data, out width, out height, out frameCount)) return null;
        }
        else
        {
            // MaxFrames: a file with a million tiny frames must not be parsed to the end just to be counted.
            var info = ISImage.Identify(new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = MaxAnimationFrames + 2 }, data);
            (width, height, frameCount) = (info.Width, info.Height, info.FrameMetadataCollection.Count);
        }
        if (frameCount < 2 || frameCount >= MaxAnimationFrames || (long)width * height * 4 * frameCount > MaxAnimationBytes) return null;

        using var img = ISImage.Load<Bgra32>(new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = MaxAnimationFrames }, data);
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

    /// <summary>
    /// Size (IHDR) and frame count of an animated PNG; false for a static PNG. The count is the
    /// larger of the declared one (acTL) and the frames really present (fcTL): the decoder reads
    /// them all, so a wrong declaration must not slip past the memory limit.
    /// </summary>
    private static bool TryReadApngInfo(byte[] d, out int width, out int height, out int frames)
    {
        width = height = frames = 0;
        static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
        bool animated = false;
        int declared = 0, present = 0;
        int pos = 8; // after the signature
        while (pos + 8 <= d.Length)
        {
            int length = BigEndian(d, pos);
            if (length < 0 || pos + 12L + length > d.Length) break; // truncated: count what was read
            string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
            if (type == "IHDR" && length >= 8)
            {
                width = BigEndian(d, pos + 8);
                height = BigEndian(d, pos + 12);
            }
            else if (type == "acTL" && length >= 4)
            {
                animated = true;
                declared = BigEndian(d, pos + 8);
            }
            else if (type == "fcTL")
            {
                present++;
            }
            else if (type == "IDAT" && !animated)
            {
                return false; // acTL must come before the image data: not animated
            }
            else if (type == "IEND")
            {
                break;
            }
            pos += 12 + length;
        }
        frames = Math.Max(declared, present);
        return animated && width > 0 && height > 0 && frames > 0;
    }

    /// <summary>Real color profiles are at most a few MB.</summary>
    private const int MaxProfileBytes = 16 * 1024 * 1024;

    /// <summary>True if the PNG's iCCP chunk inflates beyond <see cref="MaxProfileBytes"/>.</summary>
    private static bool PngProfileTooLarge(byte[] d)
    {
        static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
        int pos = 8;
        while (pos + 8 <= d.Length)
        {
            int length = BigEndian(d, pos);
            if (length < 0 || pos + 12L + length > d.Length) return false;
            string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
            if (type == "IDAT") return false; // the profile comes before the image data
            if (type == "iCCP")
            {
                int start = pos + 8, end = start + length;
                int nul = Array.IndexOf(d, (byte)0, start, Math.Min(80, length));
                if (nul < 0 || nul + 2 > end) return false;
                try
                {
                    using var inflate = new System.IO.Compression.ZLibStream(
                        new MemoryStream(d, nul + 2, end - nul - 2, writable: false), System.IO.Compression.CompressionMode.Decompress);
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
                        if ((total += read) > MaxProfileBytes) return true;
                }
                catch (InvalidDataException) { } // a broken profile: let the decoders deal with it
                return false;
            }
            pos += 12 + length;
        }
        return false;
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

        IReadOnlyList<ColorContext>? colorContexts = null;
        if ((options & BitmapCreateOptions.IgnoreColorProfile) == 0)
        {
            try
            {
                // Converted pixels are sRGB now: that is the profile a saved copy must declare (for a
                // JPEG it replaces the original one kept in the copied metadata).
                if (frame.ColorContexts is { Count: > 0 } cc)
                    colorContexts = KeepsFileColors(frame.Format) ? cc.ToList() : [Srgb];
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* unreadable profile: none */ }
        }

        return new LoadedImage
        {
            Bitmap = bitmap,
            JpegMetadata = jpegMeta,
            ColorContexts = colorContexts,
            FormatName = FormatName(decoder),
            FileSize = data.LongLength,
            AutoOriented = autoOrient,
            PageCount = decoder is TiffBitmapDecoder ? decoder.Frames.Count : 1, // GIF frames are not pages
            // Icon frames are sizes of one picture, TIFF frames are pages.
            FrameCount = decoder is TiffBitmapDecoder or IconBitmapDecoder ? 1 : Math.Max(1, decoder.Frames.Count),
        };
    }

    /// <summary>
    /// WIC converts 8-bit RGB (and palette) pixels to sRGB while decoding with the embedded profile;
    /// deeper, float, gray and CMYK pixels are left in the file's color space.
    /// </summary>
    private static bool KeepsFileColors(PixelFormat f) =>
        f == PixelFormats.Cmyk32 || f == PixelFormats.Gray2 || f == PixelFormats.Gray4 || f == PixelFormats.Gray8
        || f == PixelFormats.Gray16 || f == PixelFormats.Gray32Float || f == PixelFormats.BlackWhite
        || f == PixelFormats.Rgb48 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64
        || f == PixelFormats.Rgb128Float || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float;

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

    private static LoadedImage DecodeImageSharp(byte[] data, bool autoOrient, bool firstFrameOnly = false)
    {
        var options = new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = firstFrameOnly ? 1u : uint.MaxValue };
        // The size first: a 70-byte TGA may claim 30000 × 30000 pixels.
        var info = ISImage.Identify(options, data);
        if ((long)info.Width * info.Height > MaxPixels)
            throw new InvalidDataException($"The image is too large ({info.Width} × {info.Height}).");
        using var img = ISImage.Load<Bgra32>(options, data);
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
            int fullW = swap ? frame.PixelHeight : frame.PixelWidth, fullH = swap ? frame.PixelWidth : frame.PixelHeight;
            return (CropToAspect(preview, fullW, fullH), fullW, fullH);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// EXIF thumbnails are often 160×120 (4:3) with black bars, even for 3:2 photos: cut them to
    /// the proportions of the full image, or the preview would be stretched and show the bars.
    /// </summary>
    private static BitmapSource CropToAspect(BitmapSource thumb, int fullWidth, int fullHeight)
    {
        if (fullWidth <= 0 || fullHeight <= 0) return thumb;
        double target = (double)fullWidth / fullHeight;
        int w = thumb.PixelWidth, h = thumb.PixelHeight;
        int cropW = w, cropH = h;
        if ((double)w / h > target) cropW = (int)Math.Round(h * target);
        else cropH = (int)Math.Round(w / target);
        if (cropW < 1 || cropH < 1 || (cropW == w && cropH == h)) return thumb;
        return ImageBridge.Materialize(new CroppedBitmap(thumb, new System.Windows.Int32Rect((w - cropW) / 2, (h - cropH) / 2, cropW, cropH)));
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
