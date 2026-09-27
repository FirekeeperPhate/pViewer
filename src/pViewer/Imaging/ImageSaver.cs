using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;

namespace pViewer.Imaging;

public static class ImageSaver
{
    /// <summary>
    /// Saves in the format given by the extension. Writes to a temporary file and then replaces,
    /// so a failure halfway never destroys the original. If the original was a JPEG its metadata
    /// (date taken, camera, GPS…) is kept; the orientation is reset when the pixels were rotated upright.
    /// </summary>
    public static void Save(BitmapSource bitmap, string path, int jpegQuality, BitmapMetadata? jpegMetadata = null,
        bool resetOrientation = true)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new IOException($"«{Path.GetFileName(path)}» is read-only.");
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (ext == ".webp")
            {
                using var img = ImageBridge.ToImageSharp(bitmap);
                img.Save(temp, new WebpEncoder { Quality = Math.Clamp(jpegQuality, 1, 100) });
            }
            else
            {
                WriteWithEncoder(bitmap, temp, ext, jpegQuality, jpegMetadata, resetOrientation);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void WriteWithEncoder(BitmapSource bitmap, string path, string ext, int quality, BitmapMetadata? metadata,
        bool resetOrientation)
    {
        bool jpeg = ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif";
        BitmapSource source = jpeg || ext == ".bmp" ? ImageOps.FlattenAlpha(bitmap, Colors.White) : bitmap;

        BitmapMetadata? meta = jpeg ? PrepareJpegMetadata(metadata, resetOrientation) : null;
        try
        {
            Encode(source, path, ext, quality, meta);
        }
        catch (Exception) when (meta is not null)
        {
            // Metadata not compatible with the encoder: save without it.
            Encode(source, path, ext, quality, null);
        }
    }

    private static void Encode(BitmapSource source, string path, string ext, int quality, BitmapMetadata? meta)
    {
        BitmapEncoder encoder = ext switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            ".png" => new PngBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            ".jxr" or ".wdp" => new WmpBitmapEncoder(),
            _ => throw new NotSupportedException($"Unsupported save format: {ext}"),
        };
        encoder.Frames.Add(BitmapFrame.Create(source, null, meta, null));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
    }

    private static BitmapMetadata? PrepareJpegMetadata(BitmapMetadata? original, bool resetOrientation)
    {
        if (original is null) return null;
        try
        {
            var meta = original.Clone();
            // The pixels are already upright: the orientation goes back to "normal".
            if (resetOrientation && meta.ContainsQuery("/app1/ifd/{ushort=274}")) meta.SetQuery("/app1/ifd/{ushort=274}", (ushort)1);
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
