using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using WColor = System.Windows.Media.Color;

namespace pViewer.Imaging;

/// <summary>Text to print on the image; coordinates and sizes in image pixels.</summary>
public sealed record TextPlacement(
    string Text, double X, double Y, string FontFamily, double FontSize, bool Bold, bool Italic, WColor Color);

/// <summary>
/// Editing operations. They take a frozen BitmapSource and return a new one:
/// the original stays untouched (undo needs it). The ones that draw with WPF must
/// run on an STA thread (see <see cref="StaTask"/>).
/// </summary>
public static class ImageOps
{
    public static BitmapSource Rotate(BitmapSource src, int degrees) =>
        ImageBridge.Materialize(new TransformedBitmap(src, new RotateTransform(degrees)));

    public static BitmapSource Flip(BitmapSource src, bool horizontal) =>
        ImageBridge.Materialize(new TransformedBitmap(src, horizontal ? new ScaleTransform(-1, 1) : new ScaleTransform(1, -1)));

    public static Int32Rect ClampRect(BitmapSource src, Int32Rect r)
    {
        int x = Math.Clamp(r.X, 0, src.PixelWidth);
        int y = Math.Clamp(r.Y, 0, src.PixelHeight);
        int right = Math.Clamp(r.X + r.Width, 0, src.PixelWidth);
        int bottom = Math.Clamp(r.Y + r.Height, 0, src.PixelHeight);
        return new Int32Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    public static BitmapSource Crop(BitmapSource src, Int32Rect rect)
    {
        rect = ClampRect(src, rect);
        if (rect.Width < 1 || rect.Height < 1) return src;
        return ImageBridge.Materialize(new CroppedBitmap(src, rect));
    }

    public static BitmapSource Resize(BitmapSource src, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        bool down = (long)width * height < (long)src.PixelWidth * src.PixelHeight;
        return ImageBridge.Process(src, img => img.Mutate(x => x.Resize(width, height,
            down ? KnownResamplers.Lanczos3 : KnownResamplers.Bicubic)));
    }

    /// <summary>Scales down proportionally to fit within maxW×maxH (never enlarges).</summary>
    public static BitmapSource FitWithin(BitmapSource src, int maxW, int maxH)
    {
        double ratio = Math.Min((double)maxW / src.PixelWidth, (double)maxH / src.PixelHeight);
        if (ratio >= 1) return src;
        return Resize(src, (int)Math.Round(src.PixelWidth * ratio), (int)Math.Round(src.PixelHeight * ratio));
    }

    public static BitmapSource Invert(BitmapSource src) => ImageBridge.Process(src, i => i.Mutate(x => x.Invert()));
    public static BitmapSource Grayscale(BitmapSource src) => ImageBridge.Process(src, i => i.Mutate(x => x.Grayscale()));
    public static BitmapSource Sepia(BitmapSource src) => ImageBridge.Process(src, i => i.Mutate(x => x.Sepia()));

    /// <summary>Pure black and white (formerly "1 bit").</summary>
    public static BitmapSource BlackWhite(BitmapSource src) =>
        ImageBridge.Process(src, img => img.ProcessPixelRows(rows =>
        {
            // By luminance, keeping alpha (a threshold on the whole pixel would turn the
            // transparent areas of a PNG solid black).
            for (int y = 0; y < rows.Height; y++)
            {
                foreach (ref Bgra32 p in rows.GetRowSpan(y))
                {
                    byte v = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B >= 128 ? (byte)255 : (byte)0;
                    p = new Bgra32(v, v, v, p.A);
                }
            }
        }));

    /// <param name="brightness">-100..100</param>
    /// <param name="contrast">-100..100</param>
    public static BitmapSource BrightnessContrast(BitmapSource src, double brightness, double contrast) =>
        ImageBridge.Process(src, i => i.Mutate(x =>
        {
            if (brightness != 0) x.Brightness((float)(1 + brightness / 100));
            if (contrast != 0) x.Contrast((float)(1 + contrast / 100));
        }));

    public static BitmapSource Sharpen(BitmapSource src, double sigma) => sigma <= 0 ? src
        : ImageBridge.Process(src, i => i.Mutate(x => x.GaussianSharpen((float)sigma)));

    public static BitmapSource Blur(BitmapSource src, double sigma) => sigma <= 0 ? src
        : ImageBridge.Process(src, i => i.Mutate(x => x.GaussianBlur((float)sigma)));

    /// <param name="hue">-180..180 degrees</param>
    /// <param name="saturation">-100..100</param>
    public static BitmapSource HueSaturation(BitmapSource src, double hue, double saturation) =>
        ImageBridge.Process(src, i => i.Mutate(x =>
        {
            if (hue != 0) x.Hue((float)hue);
            if (saturation != 0) x.Saturate((float)(1 + saturation / 100));
        }));

    public static BitmapSource AddBorder(BitmapSource src, int thickness, WColor color) =>
        ImageBridge.Process(src, i => i.Mutate(x => x.Pad(i.Width + thickness * 2, i.Height + thickness * 2,
            SixLabors.ImageSharp.Color.FromRgba(color.R, color.G, color.B, color.A))));

    /// <summary>Reduces red where it clearly dominates green and blue (red eyes).</summary>
    public static BitmapSource RedEye(BitmapSource src, Int32Rect area)
    {
        area = ClampRect(src, area);
        if (area.Width < 1 || area.Height < 1) return src;
        return ImageBridge.Process(src, img => img.ProcessPixelRows(rows =>
        {
            for (int y = area.Y; y < area.Y + area.Height; y++)
            {
                Span<Bgra32> row = rows.GetRowSpan(y);
                for (int x = area.X; x < area.X + area.Width; x++)
                {
                    ref Bgra32 p = ref row[x];
                    int avg = (p.G + p.B) / 2;
                    if (p.R > 60 && p.R > avg * 1.5) p.R = (byte)avg;
                }
            }
        }));
    }

    // ---- Operations that draw with WPF (STA thread) ----

    /// <param name="aliased">Hard edges: shapes on integer pixel edges stay crisp instead of
    /// getting half-transparent anti-aliased borders.</param>
    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw, bool aliased = false)
    {
        var visual = new DrawingVisual();
        if (aliased) RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
        using (var dc = visual.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static Rect FullRect(BitmapSource src) => new(0, 0, src.PixelWidth, src.PixelHeight);

    public static BitmapSource DrawRectangle(BitmapSource src, Int32Rect rect, WColor color, double thickness, bool fill)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return Render(src.PixelWidth, src.PixelHeight, dc =>
        {
            dc.DrawImage(src, FullRect(src));
            var r = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            if (fill) dc.DrawRectangle(brush, null, r);
            else
            {
                // Four filled bands on whole pixels, centred on the edges like a pen would be: a pen
                // of odd width centred on an integer edge would fall on half pixels (soft edges).
                int t = Math.Max(1, (int)Math.Round(thickness));
                int before = t / 2;
                int left = rect.X - before, top = rect.Y - before;
                int outerW = rect.Width + t, outerH = rect.Height + t;
                dc.DrawRectangle(brush, null, new Rect(left, top, outerW, t));                    // top
                dc.DrawRectangle(brush, null, new Rect(left, top + outerH - t, outerW, t));       // bottom
                dc.DrawRectangle(brush, null, new Rect(left, top + t, t, outerH - 2 * t));        // left
                dc.DrawRectangle(brush, null, new Rect(left + outerW - t, top + t, t, outerH - 2 * t)); // right
            }
        }, aliased: true);
    }

    public static FormattedText CreateFormattedText(TextPlacement t)
    {
        var brush = new SolidColorBrush(t.Color);
        brush.Freeze();
        var typeface = new Typeface(new FontFamily(t.FontFamily),
            t.Italic ? FontStyles.Italic : FontStyles.Normal,
            t.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        return new FormattedText(t.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, Math.Max(1, t.FontSize), brush, 1.0);
    }

    public static BitmapSource DrawText(BitmapSource src, TextPlacement text) =>
        Render(src.PixelWidth, src.PixelHeight, dc =>
        {
            dc.DrawImage(src, FullRect(src));
            dc.DrawText(CreateFormattedText(text), new Point(text.X, text.Y));
        });

    /// <summary>Joins side-by-side pages (manga/comic mode) into a single image.</summary>
    public static BitmapSource Compose(IReadOnlyList<BitmapSource> pages, WColor background)
    {
        if (pages.Count == 1) return pages[0];
        int width = pages.Sum(p => p.PixelWidth);
        int height = pages.Max(p => p.PixelHeight);
        var bg = new SolidColorBrush(background);
        bg.Freeze();
        return Render(width, height, dc =>
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
            double x = 0;
            foreach (var p in pages)
            {
                // Whole pixels: a half-pixel offset (odd height difference) would resample the page.
                dc.DrawImage(p, new Rect(x, Math.Floor((height - p.PixelHeight) / 2.0), p.PixelWidth, p.PixelHeight));
                x += p.PixelWidth;
            }
        });
    }

    /// <summary>Flattens transparency onto a background color (for JPEG/BMP).</summary>
    public static BitmapSource FlattenAlpha(BitmapSource src, WColor background)
    {
        if (!ImageBridge.MayHaveAlpha(src.Format)) return src;
        var bg = SixLabors.ImageSharp.Color.FromRgb(background.R, background.G, background.B);
        var flat = ImageBridge.Process(src, i => i.Mutate(x => x.BackgroundColor(bg)));
        var bgr = new FormatConvertedBitmap(flat, PixelFormats.Bgr24, null, 0);
        bgr.Freeze();
        return bgr;
    }
}
