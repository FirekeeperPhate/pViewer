using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pViewer.Imaging;

namespace pViewer.Services;

public static class ClipboardImage
{
    /// <summary>
    /// Copies an image as both the standard bitmap (understood by every program) and PNG, which
    /// keeps transparency (used by pViewer itself, browsers, Office, GIMP…).
    /// </summary>
    public static void Set(BitmapSource bitmap)
    {
        var data = new DataObject();
        data.SetImage(bitmap);
        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(png);
        png.Position = 0;
        data.SetData("PNG", png, false);
        Clipboard.SetDataObject(data, copy: true);
    }

    /// <summary>
    /// Reads an image from the clipboard. Prefers the PNG format (keeps transparency);
    /// the standard DIB often has a zero alpha channel, so it is read as opaque.
    /// </summary>
    public static BitmapSource? Get()
    {
        if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream png)
        {
            try
            {
                var bytes = Imaging.ImageDecoder.WithoutPngBombs(png.ToArray());
                var decoder = BitmapDecoder.Create(new MemoryStream(bytes, writable: false),
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();
                return frame;
            }
            catch (Exception) { /* fall back to the DIB */ }
        }

        if (!Clipboard.ContainsImage()) return null;
        var image = Clipboard.GetImage();
        if (image is null) return null;
        return ImageBridge.Materialize(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0));
    }
}
