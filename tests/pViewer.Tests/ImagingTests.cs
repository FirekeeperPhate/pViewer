using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pViewer.Core.Sources;
using pViewer.Imaging;
using pViewer.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using WColor = System.Windows.Media.Color;
using WColors = System.Windows.Media.Colors;

namespace pViewer.Tests;

internal static class Pixels
{
    public static WColor At(BitmapSource bmp, int x, int y)
    {
        var bgra = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return WColor.FromArgb(px[3], px[2], px[1], px[0]);
    }

    public static bool IsReddish(WColor c) => c.R > 180 && c.B < 90;
    public static bool IsBluish(WColor c) => c.B > 180 && c.R < 90;

    public static BitmapSource Solid(int w, int h, WColor color)
    {
        var data = new byte[w * h * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = color.B; data[i + 1] = color.G; data[i + 2] = color.R; data[i + 3] = color.A; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, data, w * 4);
        bmp.Freeze();
        return bmp;
    }
}

public class ImageDecoderTests
{
    [Fact]
    public void AppliesExifRotation()
    {
        var jpeg = TestImages.JpegWithOrientation(40, 20, 6);
        var rotated = ImageDecoder.Decode(jpeg, "a.jpg", autoOrient: true);
        Assert.Equal(20, rotated.PixelWidth);
        Assert.Equal(40, rotated.PixelHeight);
        Assert.Equal("JPEG", rotated.FormatName);
        Assert.NotNull(rotated.JpegMetadata);

        var raw = ImageDecoder.Decode(jpeg, "a.jpg", autoOrient: false);
        Assert.Equal(40, raw.PixelWidth);
    }

    [Fact]
    public void Orientation3TurnsImageUpsideDown()
    {
        var img = ImageDecoder.Decode(TestImages.JpegWithOrientation(40, 20, 3), "a.jpg", true);
        Assert.True(Pixels.IsBluish(Pixels.At(img.Bitmap, 2, 10)));  // la metà blu ora è a sinistra
        Assert.True(Pixels.IsReddish(Pixels.At(img.Bitmap, 37, 10)));
    }

    [Fact]
    public void FallsBackToImageSharpForFormatsWithoutWicCodec()
    {
        using var img = new Image<Rgba32>(7, 5, new Rgba32(1, 2, 3));
        using var ms = new MemoryStream();
        img.SaveAsQoi(ms);
        var loaded = ImageDecoder.Decode(ms.ToArray(), "x.qoi", true);
        Assert.Equal(7, loaded.PixelWidth);
    }

    [Fact]
    public void GarbageGivesFriendlyError()
    {
        var ex = Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode([1, 2, 3, 4], "rotto.heic", true));
        Assert.Contains("HEIF", ex.Message);
    }

    [Fact]
    public void ThumbnailPreviewReportsFullSizeAfterRotation()
    {
        // ImageSharp non scrive miniature EXIF: senza miniatura l'anteprima deve essere semplicemente null.
        Assert.Null(ImageDecoder.TryDecodeEmbeddedPreview(TestImages.JpegWithOrientation(40, 20, 6), true));
        Assert.Null(ImageDecoder.TryDecodeEmbeddedPreview(TestImages.Png(4, 4), true));
    }
}

public class ImageOpsTests
{
    private static BitmapSource Split() => ImageDecoder.Decode(TestImages.JpegWithOrientation(40, 20, 1), "a.jpg", true).Bitmap;

    [Fact]
    public void RotateAndFlip()
    {
        var src = Split();
        var r = ImageOps.Rotate(src, 90);
        Assert.Equal((20, 40), (r.PixelWidth, r.PixelHeight));
        Assert.True(Pixels.IsReddish(Pixels.At(r, 10, 2)));   // la sinistra rossa finisce in alto
        var f = ImageOps.Flip(src, horizontal: true);
        Assert.True(Pixels.IsBluish(Pixels.At(f, 2, 10)));
    }

    [Fact]
    public void CropIsClampedToImage()
    {
        var c = ImageOps.Crop(Split(), new Int32Rect(30, 10, 50, 50));
        Assert.Equal((10, 10), (c.PixelWidth, c.PixelHeight));
        Assert.True(Pixels.IsBluish(Pixels.At(c, 5, 5)));
    }

    [Fact]
    public void ResizeAndFitWithin()
    {
        var src = Split();
        Assert.Equal((10, 5), (ImageOps.Resize(src, 10, 5).PixelWidth, ImageOps.Resize(src, 10, 5).PixelHeight));
        var fit = ImageOps.FitWithin(src, 20, 20);
        Assert.Equal((20, 10), (fit.PixelWidth, fit.PixelHeight));
        Assert.Same(src, ImageOps.FitWithin(src, 100, 100)); // mai ingrandita
    }

    [Fact]
    public void RedEyeOnlyInsideTheArea()
    {
        var src = Pixels.Solid(10, 10, WColor.FromRgb(220, 40, 40));
        var fixedImg = ImageOps.RedEye(src, new Int32Rect(0, 0, 5, 10));
        Assert.True(Pixels.At(fixedImg, 2, 5).R < 60);
        Assert.Equal(220, Pixels.At(fixedImg, 8, 5).R);
    }

    [Fact]
    public void BorderAddsPadding()
    {
        var b = ImageOps.AddBorder(Pixels.Solid(10, 6, WColors.Red), 3, WColors.White);
        Assert.Equal((16, 12), (b.PixelWidth, b.PixelHeight));
        Assert.Equal(WColors.White, Pixels.At(b, 0, 0));
    }

    [Fact]
    public async Task DrawingOperationsRunOnStaThread()
    {
        var src = Pixels.Solid(40, 30, WColors.White);
        var rect = await StaTask.Run(() => ImageOps.DrawRectangle(src, new Int32Rect(5, 5, 10, 10), WColors.Black, 0, fill: true));
        Assert.Equal(0, Pixels.At(rect, 10, 10).R);
        Assert.Equal(255, Pixels.At(rect, 30, 25).R);

        var text = await StaTask.Run(() => ImageOps.DrawText(src,
            new TextPlacement("WW", 0, 0, "Arial", 24, true, false, WColors.Black)));
        bool anyDark = false;
        for (int x = 0; x < 30 && !anyDark; x++)
            for (int y = 0; y < 28 && !anyDark; y++)
                anyDark = Pixels.At(text, x, y).R < 100;
        Assert.True(anyDark);

        var pair = await StaTask.Run(() => ImageOps.Compose([Pixels.Solid(10, 20, WColors.Red), Pixels.Solid(6, 10, WColors.Blue)], WColors.White));
        Assert.Equal((16, 20), (pair.PixelWidth, pair.PixelHeight));
        Assert.Equal(WColors.White, Pixels.At(pair, 12, 1)); // pagina più bassa centrata verticalmente
    }
}

public class ImageSaverTests
{
    [Fact]
    public void JpegKeepsMetadataAndResetsOrientation()
    {
        using var tmp = new TempFolder();
        var loaded = ImageDecoder.Decode(TestImages.JpegWithOrientation(40, 20, 6, make: "Pentax"), "a.jpg", true);
        string path = tmp.File("out.jpg");
        ImageSaver.Save(loaded.Bitmap, path, 90, loaded.JpegMetadata);

        using var reread = SixLabors.ImageSharp.Image.Load(path);
        Assert.Equal((20, 40), (reread.Width, reread.Height));
        var exif = reread.Metadata.ExifProfile!;
        Assert.True(exif.TryGetValue(ExifTag.Make, out var make));
        Assert.Equal("Pentax", make!.Value);
        if (exif.TryGetValue(ExifTag.Orientation, out var o)) Assert.Equal((ushort)1, o!.Value);

        // Riaprendolo con la rotazione automatica non deve ruotare di nuovo.
        var again = ImageDecoder.Decode(File.ReadAllBytes(path), path, true);
        Assert.Equal(20, again.PixelWidth);
    }

    [Theory]
    [InlineData("x.png")]
    [InlineData("x.webp")]
    [InlineData("x.bmp")]
    [InlineData("x.tif")]
    [InlineData("x.gif")]
    public void SavesFormatsByExtension(string name)
    {
        using var tmp = new TempFolder();
        string path = tmp.File(name);
        ImageSaver.Save(Pixels.Solid(12, 8, WColor.FromArgb(128, 255, 0, 0)), path, 90);
        var loaded = ImageDecoder.Decode(File.ReadAllBytes(path), name, true);
        Assert.Equal((12, 8), (loaded.PixelWidth, loaded.PixelHeight));
        Assert.Single(Directory.GetFiles(tmp.Path)); // nessun file temporaneo rimasto
    }

    [Fact]
    public void OverwritesExistingFile()
    {
        using var tmp = new TempFolder();
        string path = tmp.File("a.png");
        File.WriteAllBytes(path, TestImages.Png(3, 3));
        ImageSaver.Save(Pixels.Solid(9, 9, WColors.Red), path, 90);
        Assert.Equal(9, ImageDecoder.Decode(File.ReadAllBytes(path), path, true).PixelWidth);
    }
}

public class EditSessionTests
{
    [Fact]
    public void UndoRedoAndSavedState()
    {
        var a = Pixels.Solid(2, 2, WColors.Red);
        var b = Pixels.Solid(2, 2, WColors.Green);
        var c = Pixels.Solid(2, 2, WColors.Blue);
        var s = new EditSession(a);
        Assert.False(s.IsModified);
        s.Apply(b);
        s.Apply(c);
        Assert.True(s.IsModified);
        Assert.True(s.Undo());
        Assert.Same(b, s.Current);
        Assert.True(s.Redo());
        Assert.Same(c, s.Current);
        s.MarkSaved();
        Assert.False(s.IsModified);
        s.Undo();
        Assert.True(s.IsModified);
        s.Apply(a);
        Assert.False(s.CanRedo); // una nuova modifica cancella il "ripeti"
    }
}

public class BatchProcessorTests
{
    [Fact]
    public async Task ConvertsFolderAndSkipsExistingOutputs()
    {
        using var tmp = new TempFolder();
        File.WriteAllBytes(tmp.File("a.png"), TestImages.Png(8, 4));
        File.WriteAllBytes(tmp.File("b.jpg"), TestImages.JpegWithOrientation(8, 4, 6));
        using var src = FolderSource.Open(tmp.Path);
        string output = Path.Combine(tmp.Path, "Convertite");
        var job = new BatchJob(BatchOperation.Convert, output, 0, 0, 0, ".webp", 80, 10, AutoOrient: true);

        var first = await BatchProcessor.RunAsync(src, job, null, CancellationToken.None);
        Assert.Equal(2, first.Written);
        Assert.Empty(first.Errors);
        var b = ImageDecoder.Decode(File.ReadAllBytes(Path.Combine(output, "b.webp")), "b.webp", true);
        Assert.Equal((4, 8), (b.PixelWidth, b.PixelHeight)); // raddrizzata

        var second = await BatchProcessor.RunAsync(src, job, null, CancellationToken.None);
        Assert.Equal(0, second.Written);
        Assert.Equal(2, second.Skipped);
    }

    [Fact]
    public async Task ResizesArchivePagesIntoFolder()
    {
        using var tmp = new TempFolder();
        string zip = tmp.File("fumetto.cbz");
        TestImages.Zip(zip, ("01.png", TestImages.Png(100, 50)), ("02.png", TestImages.Png(60, 60)));
        using var src = ArchiveSource.Open(zip);
        string output = Path.Combine(tmp.Path, "out");
        var job = new BatchJob(BatchOperation.Resize, output, 0, 50, 50, null, 80, 10, true);
        var result = await BatchProcessor.RunAsync(src, job, null, CancellationToken.None);
        Assert.Equal(2, result.Written);
        var p1 = ImageDecoder.Decode(File.ReadAllBytes(Path.Combine(output, "01.png")), "01.png", true);
        Assert.Equal((50, 25), (p1.PixelWidth, p1.PixelHeight));
    }
}
