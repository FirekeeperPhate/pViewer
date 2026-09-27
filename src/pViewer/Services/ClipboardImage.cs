using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pViewer.Imaging;

namespace pViewer.Services;

public static class ClipboardImage
{
    /// <summary>
    /// Legge un'immagine dagli appunti. Preferisce il formato PNG (conserva la trasparenza);
    /// il DIB standard spesso ha il canale alfa a zero, quindi viene letto come opaco.
    /// </summary>
    public static BitmapSource? Get()
    {
        if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream png)
        {
            try
            {
                var decoder = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();
                return frame;
            }
            catch (Exception) { /* si prova col DIB */ }
        }

        if (!Clipboard.ContainsImage()) return null;
        var image = Clipboard.GetImage();
        if (image is null) return null;
        return ImageBridge.Materialize(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0));
    }
}
