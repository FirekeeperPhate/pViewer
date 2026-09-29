using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;

namespace pViewer.Imaging;

public static class ImageSaver
{
    /// <summary>
    /// Saves in the format given by the extension. Writes to a temporary file and then replaces,
    /// so a failure halfway never destroys the original. If the original was a JPEG its metadata
    /// (date taken, camera, GPS…) is kept; the orientation is reset when the pixels were rotated upright.
    /// The color profile of the original, if any, is written again when the format can hold it.
    /// <paramref name="losslessWebp"/>: the original was a lossless WebP (kept lossless in WebP).
    /// </summary>
    public static void Save(BitmapSource bitmap, string path, int jpegQuality, BitmapMetadata? jpegMetadata = null,
        bool resetOrientation = true, IReadOnlyList<ColorContext>? colorContexts = null, bool losslessWebp = false)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new IOException($"“{Path.GetFileName(path)}” is read-only.");
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        // Short name: appending to the original name could exceed the 255-character limit.
        string temp = Path.Combine(dir, $".pv{Guid.NewGuid():N}.tmp");
        try
        {
            if (ext == ".webp")
            {
                using var img = ImageBridge.ToImageSharp(bitmap);
                if (MatchingProfile(colorContexts, PixelFormats.Bgra32) is { } icc) img.Metadata.IccProfile = new IccProfile(ProfileBytes(icc));
                // A lossless original stays lossless (ImageSharp would otherwise pick lossy).
                img.Save(temp, new WebpEncoder
                {
                    Quality = Math.Clamp(jpegQuality, 1, 100),
                    FileFormat = losslessWebp ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy,
                });
            }
            else if (ext == ".gif")
            {
                // ImageSharp quantizes with a transparent colour and an adaptive palette; the WIC GIF
                // encoder would drop transparency and use a fixed palette.
                using var img = ImageBridge.ToImageSharp(bitmap);
                // GIF transparency is on/off: normalize alpha, and flag the frame, otherwise the
                // encoder writes no transparent colour at all.
                bool anyTransparent = false;
                img.ProcessPixelRows(rows =>
                {
                    for (int y = 0; y < rows.Height; y++)
                    {
                        foreach (ref var p in rows.GetRowSpan(y))
                        {
                            if (p.A < 128) { p = default; anyTransparent = true; }
                            else p.A = 255;
                        }
                    }
                });
                if (anyTransparent) img.Frames.RootFrame.Metadata.GetGifMetadata().HasTransparency = true;
                img.Save(temp, new GifEncoder());
            }
            else
            {
                WriteWithEncoder(bitmap, temp, ext, jpegQuality, jpegMetadata, resetOrientation, colorContexts);
            }
            ReplaceOrMove(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Puts the new file in place. Over an existing file, Replace keeps what belongs to the file
    /// rather than to its content (creation date, hidden attribute, permissions); some drives and
    /// shares do not support it, so a plain move is the fallback.
    /// </summary>
    private static void ReplaceOrMove(string temp, string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                if (!File.Exists(temp)) throw; // the replace got halfway: never move over it blindly
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteWithEncoder(BitmapSource bitmap, string path, string ext, int quality, BitmapMetadata? metadata,
        bool resetOrientation, IReadOnlyList<ColorContext>? colorContexts)
    {
        bool jpeg = ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif";
        BitmapSource source = jpeg || ext == ".bmp" ? ImageOps.FlattenAlpha(bitmap, Colors.White) : bitmap;
        // BMP readers other than Windows know only the classic depths: 16-bit or HDR images would be
        // written as 64 bits per pixel.
        // PNG has no CMYK: convert here, so the profile is chosen for the pixels really written.
        if ((ext == ".bmp" && !IsClassicBmpFormat(source.Format)) || (ext == ".png" && source.Format == PixelFormats.Cmyk32))
        {
            // Copied, not frozen: freezing a conversion of an image decoded on another thread fails.
            source = ImageBridge.Materialize(new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0));
        }

        BitmapMetadata? meta = jpeg ? PrepareJpegMetadata(metadata, resetOrientation) : null;
        // For a JPEG the profile is passed even with the copied metadata: it replaces the original
        // one held there, which may no longer describe the pixels. BMP and GIF get none.
        ColorContext? profile = ext is ".png" or ".tif" or ".tiff" or ".jxr" or ".wdp" || jpeg
            ? MatchingProfile(colorContexts, source.Format) : null;
        try
        {
            Encode(source, path, ext, quality, meta, profile);
        }
        catch (Exception) when (meta is not null || profile is not null)
        {
            // Metadata or profile not compatible with the encoder: save without them.
            Encode(source, path, ext, quality, null, null);
        }
    }

    private static bool IsClassicBmpFormat(PixelFormat f) =>
        f == PixelFormats.Bgr24 || f == PixelFormats.Bgr32 || f == PixelFormats.Indexed8 || f == PixelFormats.Indexed4
        || f == PixelFormats.Indexed1 || f == PixelFormats.BlackWhite;

    private static bool IsGray(PixelFormat f) =>
        f == PixelFormats.Gray2 || f == PixelFormats.Gray4 || f == PixelFormats.Gray8 || f == PixelFormats.Gray16
        || f == PixelFormats.Gray32Float || f == PixelFormats.BlackWhite;

    /// <summary>
    /// The profile that fits the pixels being saved: an edit may have turned a grayscale or CMYK
    /// image into RGB, and an RGB file with a CMYK profile would be invalid. RGB pixels with no
    /// fitting profile get sRGB (what the conversion to RGB produced).
    /// </summary>
    private static ColorContext? MatchingProfile(IReadOnlyList<ColorContext>? contexts, PixelFormat format)
    {
        if (contexts is null) return null;
        string wanted = format == PixelFormats.Cmyk32 ? "CMYK" : IsGray(format) ? "GRAY" : "RGB ";
        foreach (var context in contexts)
        {
            try
            {
                byte[] bytes = ProfileBytes(context);
                // ICC header: the data color space is at offset 16.
                if (bytes.Length >= 20 && System.Text.Encoding.ASCII.GetString(bytes, 16, 4) == wanted) return context;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* unreadable: skip it */ }
        }
        return wanted == "RGB " ? ImageDecoder.Srgb : null;
    }

    private static byte[] ProfileBytes(ColorContext context)
    {
        using var stream = context.OpenProfileStream();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void Encode(BitmapSource source, string path, string ext, int quality, BitmapMetadata? meta,
        ColorContext? profile)
    {
        BitmapEncoder encoder = ext switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            ".png" => new PngBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            ".jxr" or ".wdp" => new WmpBitmapEncoder(),
            _ => throw new NotSupportedException($"Unsupported save format: {ext}"),
        };
        encoder.Frames.Add(BitmapFrame.Create(source, null, meta, profile is null ? null : new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>([profile])));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
    }

    private static void TrySetQuery(BitmapMetadata meta, string query, object value)
    {
        try
        {
            if (meta.ContainsQuery(query)) meta.SetQuery(query, value);
        }
        catch (Exception) { /* not present in this metadata layout */ }
    }

    private static BitmapMetadata? PrepareJpegMetadata(BitmapMetadata? original, bool resetOrientation)
    {
        if (original is null) return null;
        try
        {
            var meta = original.Clone();
            // The pixels are already upright: the orientation goes back to "normal".
            if (resetOrientation && meta.ContainsQuery("/app1/ifd/{ushort=274}")) meta.SetQuery("/app1/ifd/{ushort=274}", (ushort)1);
            // The XMP copy of the tag too, or apps that read XMP would rotate the image again.
            if (resetOrientation) TrySetQuery(meta, "/xmp/tiff:Orientation", "1");
            // The EXIF thumbnail would no longer match the content.
            if (meta.ContainsQuery("/app1/thumb")) meta.RemoveQuery("/app1/thumb");
            return meta;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
