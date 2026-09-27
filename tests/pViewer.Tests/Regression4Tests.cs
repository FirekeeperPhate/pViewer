using System.Text;
using SixLabors.ImageSharp;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pViewer.Core;
using pViewer.Imaging;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;

namespace pViewer.Tests;

/// <summary>Regressions for the fourth review pass.</summary>
public class Regression4Tests
{
    [Fact]
    public void GoToInTwoPageModePairsFromTheTargetPage()
    {
        var nav = new PageNavigator { Layout = PageLayout.Comic };
        nav.Reset(10);
        nav.GoTo(3);                                     // e.g. the file just saved with Save As
        Assert.Equal([3, 4], nav.VisibleIndices());      // a pair, not page 3 alone
        nav.Previous();
        Assert.Equal([1, 2], nav.VisibleIndices());      // page 2 not skipped going back
        nav.Previous();
        Assert.Equal([0], nav.VisibleIndices());
    }

    private static BitmapSource Gray16(int w, int h, double dpi = 96)
    {
        var data = new ushort[w * h];
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)(i * 997);
        var bmp = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Gray16, null, data, w * 2);
        bmp.Freeze();
        return bmp;
    }

    [Fact]
    public void GeometricEditsKeepThePixelFormat()
    {
        var src = Gray16(8, 4);
        Assert.Equal(PixelFormats.Gray16, ImageOps.Rotate(src, 90).Format);
        Assert.Equal(PixelFormats.Gray16, ImageOps.Flip(src, true).Format);
        Assert.Equal(PixelFormats.Gray16, ImageOps.Crop(src, new Int32Rect(1, 1, 4, 2)).Format);
    }

    [Fact]
    public async Task EditsKeepTheResolution()
    {
        var src = Gray16(20, 20, dpi: 300);
        Assert.Equal(300, ImageOps.Invert(src).DpiX, 3);
        Assert.Equal(300, ImageOps.Resize(src, 10, 10).DpiY, 3);
        var rect = await StaTask.Run(() => ImageOps.DrawRectangle(src, new Int32Rect(2, 2, 5, 5), Colors.Red, 1, fill: false));
        Assert.Equal(300, rect.DpiX, 3);
    }

    [Fact]
    public async Task BmpIsAlwaysWrittenWithAClassicDepth()
    {
        using var tmp = new TempFolder();
        string path = tmp.File("deep.bmp");
        await StaTask.Run(() => { ImageSaver.Save(Gray16(6, 6), path, 90); return true; });
        byte[] file = File.ReadAllBytes(path);
        Assert.Equal(24, BitConverter.ToUInt16(file, 28)); // biBitCount
    }

    [Fact]
    public async Task ColorProfileSurvivesAnEditAndASave()
    {
        string icm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\spool\drivers\color\sRGB Color Space Profile.icm");
        if (!File.Exists(icm)) return; // no profile to test with on this machine
        using var img = new SixLabors.ImageSharp.Image<Rgba32>(16, 16, new Rgba32(200, 30, 30));
        img.Metadata.IccProfile = new IccProfile(File.ReadAllBytes(icm));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);

        var loaded = ImageDecoder.Decode(ms.ToArray(), "p.png", true);
        Assert.NotNull(loaded.ColorContexts);
        var edited = ImageOps.Invert(loaded.Bitmap);

        using var tmp = new TempFolder();
        foreach (var (name, marker) in new[] { ("out.png", "iCCP"), ("out.webp", "ICCP") })
        {
            string path = tmp.File(name);
            await StaTask.Run(() => { ImageSaver.Save(edited, path, 90, colorContexts: loaded.ColorContexts); return true; });
            Assert.Contains(marker, Encoding.ASCII.GetString(File.ReadAllBytes(path)));
        }
        // A TIFF keeps it too (tag 34675).
        string tif = tmp.File("out.tif");
        await StaTask.Run(() => { ImageSaver.Save(edited, tif, 90, colorContexts: loaded.ColorContexts); return true; });
        Assert.NotNull(ImageDecoder.Decode(File.ReadAllBytes(tif), "out.tif", true).ColorContexts);
    }

    [Fact]
    public void AnimatedPngAnimates()
    {
        using var img = new SixLabors.ImageSharp.Image<Rgba32>(8, 8, new Rgba32(255, 0, 0));
        using (var second = new SixLabors.ImageSharp.Image<Rgba32>(8, 8, new Rgba32(0, 0, 255)))
            img.Frames.AddFrame(second.Frames.RootFrame);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        Assert.Contains("acTL", Encoding.ASCII.GetString(ms.ToArray()));

        var loaded = ImageDecoder.Decode(ms.ToArray(), "a.png", true);
        Assert.NotNull(loaded.Animation);
        Assert.Equal(2, loaded.Animation!.Frames.Count);

        // A static PNG is still static.
        Assert.Null(ImageDecoder.Decode(TestImages.Png(8, 8), "s.png", true).Animation);
    }
}
