using System.Runtime.InteropServices;
using SkiaSharp;

namespace pViewer.Imaging;

/// <summary>
/// What pViewer asks of Skia (the graphics library of Chrome and Android, through SkiaSharp):
/// animated GIF and WebP, a second decoder for the files Windows cannot read, saving WebP, and
/// the two filters that are slow to do by hand, blur and sharpen. Everything goes in and out as
/// <see cref="PixelBuffer"/>; nothing of Skia is seen outside this file.
/// </summary>
internal static class SkiaImaging
{
    /// <summary>The pixels of a <see cref="PixelBuffer"/>, as Skia calls them. No color space: colors are never converted.</summary>
    private static SKImageInfo Plain(int width, int height) => new(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);

    private static SKImageInfo Premultiplied(int width, int height) => new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

    /// <summary>A decoded animation: every frame complete (already put over the ones before it).</summary>
    public sealed record Animation(List<PixelBuffer> Frames, List<int> DurationsMs);

    /// <summary>Size and number of frames, without decoding anything. null if Skia does not know the format.</summary>
    public static (int Width, int Height, int Frames, string Format)? Identify(byte[] data)
    {
        using var bytes = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(bytes);
        if (codec is null) return null;
        return (codec.Info.Width, codec.Info.Height, Math.Max(1, codec.FrameCount), FormatName(codec.EncodedFormat));
    }

    /// <summary>All the frames of an animated GIF or WebP.</summary>
    public static Animation DecodeAnimation(byte[] data)
    {
        using var bytes = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(bytes) ?? throw new InvalidDataException("Not an image Skia can read.");
        int width = codec.Info.Width, height = codec.Info.Height, count = codec.FrameCount;
        var frameInfo = codec.FrameInfo;
        if (count < 1 || frameInfo.Length < count) throw new InvalidDataException("No frames.");
        var info = Premultiplied(width, height);
        // Frames are put together premultiplied (Skia blends a frame over the one before only so).
        var canvas = new byte[ImageBridge.BufferSize(width, height)];
        var pinned = GCHandle.Alloc(canvas, GCHandleType.Pinned);
        try
        {
            var frames = new List<PixelBuffer>(count);
            var durations = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                // What must already be in the canvas: nothing, the frame just decoded (the usual
                // case), or an earlier one (a frame that was to be removed again after its time).
                int required = frameInfo[i].RequiredFrame;
                int prior = -1;
                if (required >= 0 && required < i)
                {
                    if (required != i - 1) Premultiply(frames[required].Data, canvas);
                    prior = required;
                }
                var result = codec.GetPixels(info, pinned.AddrOfPinnedObject(), new SKCodecOptions(i, prior));
                // A file cut short still shows what was read of its last frame.
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    if (frames.Count == 0) throw new InvalidDataException($"Frame not decoded ({result}).");
                    break;
                }
                var frame = new PixelBuffer(width, height);
                Unpremultiply(canvas, frame.Data);
                frames.Add(frame);
                durations.Add(frameInfo[i].Duration);
            }
            return new Animation(frames, durations);
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>The image, or the first frame of an animation.</summary>
    /// <param name="orientation">EXIF orientation of the pixels returned (1 = upright).</param>
    public static PixelBuffer DecodeStill(byte[] data, long maxPixels, out string format, out ushort orientation)
    {
        using var bytes = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(bytes) ?? throw new InvalidDataException("Not an image Skia can read.");
        int width = codec.Info.Width, height = codec.Info.Height;
        // The size first: a few bytes may claim 30000 × 30000 pixels.
        if (width < 1 || height < 1 || (long)width * height > maxPixels)
            throw new InvalidDataException($"The image is too large ({width} × {height}).");
        format = FormatName(codec.EncodedFormat);
        orientation = (ushort)codec.EncodedOrigin is >= 1 and <= 8 and var o ? o : (ushort)1;
        var pixels = new PixelBuffer(width, height);
        var pinned = GCHandle.Alloc(pixels.Data, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(Plain(width, height), pinned.AddrOfPinnedObject(), new SKCodecOptions(0));
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException($"Image not decoded ({result}).");
        }
        finally
        {
            pinned.Free();
        }
        return pixels;
    }

    private static string FormatName(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Webp => "WebP",
        SKEncodedImageFormat.Jpeg => "JPEG",
        _ => format.ToString().ToUpperInvariant(),
    };

    /// <summary>WebP bytes of the image.</summary>
    /// <param name="quality">1-100 for lossy; lossless always keeps every pixel.</param>
    /// <param name="iccProfile">Color profile to write into the file, if Skia accepts it.</param>
    public static byte[] EncodeWebp(PixelBuffer pixels, int quality, bool lossless, byte[]? iccProfile)
    {
        using var colorSpace = ColorSpace(iccProfile);
        var info = Plain(pixels.Width, pixels.Height).WithColorSpace(colorSpace);
        var pinned = GCHandle.Alloc(pixels.Data, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, pinned.AddrOfPinnedObject(), pixels.Stride);
            // Lossless: the number is the effort spent on making the file smaller, not a quality.
            var options = lossless
                ? new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75)
                : new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, Math.Clamp(quality, 1, 100));
            using var encoded = pixmap.Encode(options) ?? throw new InvalidOperationException("The image could not be written as WebP.");
            return encoded.ToArray();
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>null for no profile, or one Skia does not accept (damaged, or not an RGB profile).</summary>
    private static SKColorSpace? ColorSpace(byte[]? iccProfile)
    {
        if (iccProfile is null) return null;
        try
        {
            using var profile = SKColorSpaceIccProfile.Create(iccProfile);
            return profile is null ? null : SKColorSpace.CreateIcc(profile);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static PixelBuffer Blur(PixelBuffer pixels, float sigma) =>
        Filter(pixels, bounds => SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp, null, bounds));

    /// <summary>
    /// Twice the image minus its blur, across and then down: edges get more contrast, flat areas
    /// stay as they are. Skia blurs; the subtraction is done here (its own filter for that takes
    /// several times longer on a large photo).
    /// </summary>
    public static PixelBuffer Sharpen(PixelBuffer pixels, float sigma)
    {
        PixelBuffer Along(PixelBuffer source, float sigmaX, float sigmaY)
        {
            var blurred = Filter(source, bounds => SKImageFilter.CreateBlur(sigmaX, sigmaY, SKShaderTileMode.Clamp, null, bounds));
            byte[] s = source.Data, b = blurred.Data;
            Parallel.For(0, source.Height, y =>
            {
                int end = (y + 1) * source.Stride;
                for (int i = y * source.Stride; i < end; i++) b[i] = (byte)Math.Clamp(2 * s[i] - b[i], 0, 255);
            });
            return blurred;
        }
        return Along(Along(pixels, sigma, 0), 0, sigma);
    }

    private static PixelBuffer Filter(PixelBuffer pixels, Func<SKRect, SKImageFilter> create)
    {
        int width = pixels.Width, height = pixels.Height;
        var result = new PixelBuffer(width, height);
        var source = GCHandle.Alloc(pixels.Data, GCHandleType.Pinned);
        var target = GCHandle.Alloc(result.Data, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(Plain(width, height), source.AddrOfPinnedObject(), pixels.Stride);
            using var image = SKImage.FromPixels(pixmap);
            using var surface = SKSurface.Create(Premultiplied(width, height))
                                ?? throw new InvalidOperationException($"The image is too large for this effect ({width} × {height}).");
            using var filter = create(new SKRect(0, 0, width, height));
            using var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src };
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default, paint);
            if (!surface.ReadPixels(Plain(width, height), target.AddrOfPinnedObject(), result.Stride, 0, 0))
                throw new InvalidOperationException("The effect could not be read back.");
            return result;
        }
        finally
        {
            source.Free();
            target.Free();
        }
    }

    /// <summary>Colors × alpha (what Skia blends), from plain colors.</summary>
    private static void Premultiply(byte[] plain, byte[] premultiplied)
    {
        for (int i = 0; i < plain.Length; i += 4)
        {
            int a = plain[i + 3];
            premultiplied[i] = (byte)((plain[i] * a + 127) / 255);
            premultiplied[i + 1] = (byte)((plain[i + 1] * a + 127) / 255);
            premultiplied[i + 2] = (byte)((plain[i + 2] * a + 127) / 255);
            premultiplied[i + 3] = (byte)a;
        }
    }

    internal static void Unpremultiply(byte[] premultiplied, byte[] plain)
    {
        for (int i = 0; i < premultiplied.Length; i += 4)
        {
            int a = premultiplied[i + 3];
            if (a == 255)
            {
                plain[i] = premultiplied[i]; plain[i + 1] = premultiplied[i + 1]; plain[i + 2] = premultiplied[i + 2]; plain[i + 3] = 255;
            }
            else if (a == 0)
            {
                plain[i] = plain[i + 1] = plain[i + 2] = plain[i + 3] = 0;
            }
            else
            {
                plain[i] = (byte)Math.Min(255, (premultiplied[i] * 255 + a / 2) / a);
                plain[i + 1] = (byte)Math.Min(255, (premultiplied[i + 1] * 255 + a / 2) / a);
                plain[i + 2] = (byte)Math.Min(255, (premultiplied[i + 2] * 255 + a / 2) / a);
                plain[i + 3] = (byte)a;
            }
        }
    }
}
