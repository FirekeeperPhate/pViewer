using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pViewer.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;

namespace pViewer.Tests;

/// <summary>Regressions for the fifth review pass.</summary>
public class Regression5Tests
{
    private static readonly string ColorDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        @"System32\spool\drivers\color");

    /// <summary>The sRGB profile with the red and green primaries swapped: a "wide gamut" stand-in.</summary>
    private static byte[]? SwappedProfile()
    {
        string path = Path.Combine(ColorDir, "sRGB Color Space Profile.icm");
        if (!File.Exists(path)) return null;
        byte[] p = File.ReadAllBytes(path);
        static int BE(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
        int count = BE(p, 128), r = -1, g = -1;
        for (int i = 0; i < count; i++)
        {
            int e = 132 + i * 12;
            string sig = Encoding.ASCII.GetString(p, e, 4);
            if (sig == "rXYZ") r = e;
            if (sig == "gXYZ") g = e;
        }
        if (r < 0 || g < 0) return null;
        for (int k = 4; k < 12; k++) (p[r + k], p[g + k]) = (p[g + k], p[r + k]);
        return p;
    }

    private static System.Windows.Media.Color ColorManagedPixel(string file)
    {
        var frame = BitmapDecoder.Create(new Uri(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        return Pixels.At(frame, 2, 2);
    }

    [Fact]
    public async Task WideGamutColorsLookTheSameAfterAnEditAndASave()
    {
        var profile = SwappedProfile();
        if (profile is null) return; // no profile to build the test with on this machine
        using var tmp = new TempFolder();
        string original = tmp.File("wide.png");
        using (var img = new SixLabors.ImageSharp.Image<Rgba32>(8, 8, new Rgba32(190, 110, 60)))
        {
            img.Metadata.IccProfile = new IccProfile(profile);
            img.SaveAsPng(original);
        }
        var shown = ColorManagedPixel(original);

        var loaded = ImageDecoder.Decode(File.ReadAllBytes(original), "wide.png", true);
        var edited = ImageOps.Invert(ImageOps.Invert(loaded.Bitmap));
        foreach (var name in new[] { "out.png", "out.tif", "out.jpg" })
        {
            string saved = tmp.File(name);
            await StaTask.Run(() => { ImageSaver.Save(edited, saved, 100, loaded.JpegMetadata, colorContexts: loaded.ColorContexts); return true; });
            var again = ColorManagedPixel(saved);
            // What a color-managed app shows is what pViewer showed (a few levels for JPEG).
            Assert.InRange(Math.Abs(again.R - shown.R) + Math.Abs(again.G - shown.G) + Math.Abs(again.B - shown.B), 0, 9);
        }
    }

    [Fact]
    public async Task CmykImageSavedAsPngGetsNoCmykProfile()
    {
        string swop = Path.Combine(ColorDir, "RSWOP.icm");
        if (!File.Exists(swop)) return;
        using var tmp = new TempFolder();
        string tif = tmp.File("cmyk.tif");
        await StaTask.Run(() =>
        {
            var px = new byte[8 * 8 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 10; px[i + 1] = 200; px[i + 2] = 200; px[i + 3] = 0; }
            var cmyk = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Cmyk32, null, px, 32);
            var enc = new TiffBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(cmyk, null, null,
                new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>([new ColorContext(new Uri(swop))])));
            using var fs = File.Create(tif);
            enc.Save(fs);
            return true;
        });
        var loaded = ImageDecoder.Decode(File.ReadAllBytes(tif), "cmyk.tif", true);
        Assert.Equal(PixelFormats.Cmyk32, loaded.Bitmap.Format);

        string png = tmp.File("out.png");
        await StaTask.Run(() => { ImageSaver.Save(loaded.Bitmap, png, 90, colorContexts: loaded.ColorContexts); return true; });
        var frame = BitmapDecoder.Create(new Uri(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        foreach (var context in frame.ColorContexts ?? [])
        {
            using var s = context.OpenProfileStream();
            var header = new byte[20];
            s.ReadExactly(header);
            Assert.NotEqual("CMYK", Encoding.ASCII.GetString(header, 16, 4));
        }
    }

    [Fact]
    public void AnimationTooLargeToPlayIsStillKnownAsAnimated()
    {
        using var img = new SixLabors.ImageSharp.Image<Rgba32>(8, 8, new Rgba32(255, 0, 0));
        for (int i = 0; i < 3; i++)
        {
            using var frame = new SixLabors.ImageSharp.Image<Rgba32>(8, 8, new Rgba32(0, 0, (byte)(80 * i)));
            img.Frames.AddFrame(frame.Frames.RootFrame);
        }
        using var ms = new MemoryStream();
        img.SaveAsGif(ms);
        byte[] gif = ms.ToArray();
        // Logical screen of 20000 × 20000: four frames would need far more than the animation limit.
        BitConverter.GetBytes((ushort)20000).CopyTo(gif, 6);
        BitConverter.GetBytes((ushort)20000).CopyTo(gif, 8);

        var loaded = ImageDecoder.Decode(gif, "big.gif", true);
        Assert.Null(loaded.Animation);
        Assert.True(loaded.IsAnimated); // so Ctrl+S asks for a new name instead of keeping one frame
    }

    [Fact]
    public void UndoBudgetCountsDeepPixels()
    {
        var data = new ushort[10 * 10 * 4];
        var rgba64 = BitmapSource.Create(10, 10, 96, 96, PixelFormats.Rgba64, null, data, 80);
        Assert.Equal(800, ImageBridge.EstimateBytes(rgba64));
    }
}
