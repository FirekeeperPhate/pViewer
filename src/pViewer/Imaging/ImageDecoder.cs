using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace pViewer.Imaging;

/// <summary>Un'immagine decodificata e pronta da mostrare.</summary>
public sealed class LoadedImage
{
    public required BitmapSource Bitmap { get; init; }

    /// <summary>Metadati originali (solo JPEG), per conservarli quando si salva.</summary>
    public BitmapMetadata? JpegMetadata { get; init; }

    public string FormatName { get; init; } = "";
    public long FileSize { get; init; }
    public int PixelWidth => Bitmap.PixelWidth;
    public int PixelHeight => Bitmap.PixelHeight;
}

public sealed class ImageDecodeException(string message, Exception? inner) : Exception(message, inner);

public static class ImageDecoder
{
    public static LoadedImage Decode(byte[] data, string name, bool autoOrient)
    {
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
            ".heic" or ".heif" => "Installa «Estensioni per immagini HEIF» dal Microsoft Store.",
            ".avif" => "Installa «Estensione video AV1» dal Microsoft Store.",
            ".jxl" => "Installa «Estensione immagine JPEG XL» dal Microsoft Store.",
            ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".raf"
                => "Installa «Estensione per immagini RAW» dal Microsoft Store.",
            _ => "Il file potrebbe essere danneggiato o in un formato non supportato.",
        };
        return $"Impossibile aprire «{Path.GetFileName(name)}».\n{hint}";
    }

    private static LoadedImage DecodeWic(byte[] data, bool autoOrient, BitmapCreateOptions options)
    {
        // Lo stream non va chiuso: WIC può leggere i metadati in modo pigro.
        var ms = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(ms, options, BitmapCacheOption.OnLoad);
        // Le icone contengono più dimensioni: si mostra la più grande.
        BitmapFrame frame = decoder.Frames.Count == 1
            ? decoder.Frames[0]
            : decoder.Frames.MaxBy(f => (long)f.PixelWidth * f.PixelHeight)!;

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
        };
    }

    /// <summary>
    /// Miniatura EXIF incorporata (JPEG delle fotocamere): si legge in pochi millisecondi
    /// e si mostra mentre l'immagine completa viene decodificata.
    /// </summary>
    /// <returns>La miniatura e le dimensioni dell'immagine completa (già raddrizzate).</returns>
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

    /// <summary>Raddrizza l'immagine secondo il tag EXIF Orientation (1-8).</summary>
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
