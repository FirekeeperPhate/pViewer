using pViewer.Imaging;

namespace pViewer.Tests;

public class BackgroundRemoverTests
{
    [Fact]
    public void ColorsAreGivenAsTheNetworkWasTrainedOnThem()
    {
        // Two pixels, the brightest value 200: colors divided by it, minus the training average.
        var square = new PixelBuffer([10, 100, 200, 255, 0, 0, 50, 255], 2, 1);
        float[] input = BackgroundRemover.ToInput(square);
        Assert.Equal(6, input.Length); // red plane, green plane, blue plane
        Assert.Equal(200 / 200f - 0.485f, input[0], 5);   // red of the first pixel
        Assert.Equal(50 / 200f - 0.485f, input[1], 5);    // red of the second
        Assert.Equal(100 / 200f - 0.456f, input[2], 5);   // green of the first
        Assert.Equal(10 / 200f - 0.406f, input[4], 5);    // blue of the first
    }

    [Fact]
    public void MaskIsStretchedAndBecomesTheTransparency()
    {
        Assert.Equal([0f, 0.5f, 1f], BackgroundRemover.Stretch([-2f, 0f, 2f]));
        // A 2 × 2 mask (left half subject) over a 4 × 2 picture that is half transparent already.
        var image = new PixelBuffer(4, 2);
        for (int i = 0; i < image.Data.Length; i += 4) { image.Data[i] = 10; image.Data[i + 1] = 20; image.Data[i + 2] = 30; image.Data[i + 3] = 128; }
        var result = BackgroundRemover.ApplyMask(image, [1, 0, 1, 0], 2);
        Assert.Equal((4, 2), (result.Width, result.Height));
        Assert.Equal(128, result.Data[3]);                 // subject: alpha as it was
        Assert.Equal(0, result.Data[15]);                  // last pixel of the first row: background
        Assert.Equal((10, 20, 30), (result.Data[12], result.Data[13], result.Data[14])); // colors untouched
        Assert.Equal(128, image.Data[15]); // the original buffer is not changed
    }

    /// <summary>
    /// With the real model (set PVIEWER_TEST_MODEL to isnet-general-use.onnx; skipped otherwise):
    /// a dark disc on a light background keeps the disc and loses the background.
    /// </summary>
    [Fact]
    public void RealModelCutsOutASubject()
    {
        string? model = Environment.GetEnvironmentVariable("PVIEWER_TEST_MODEL");
        if (model is null || !File.Exists(model)) return;
        BackgroundRemover.ModelPath = model;
        Assert.True(BackgroundRemover.ModelReady);
        string? photo = Environment.GetEnvironmentVariable("PVIEWER_TEST_PHOTO");
        PixelBuffer image;
        if (photo is not null) image = ImageBridge.ToPixels(ImageDecoder.Decode(File.ReadAllBytes(photo), photo, true).Bitmap);
        else
        {
            image = new PixelBuffer(400, 300);
            for (int y = 0; y < 300; y++)
                for (int x = 0; x < 400; x++)
                {
                    int i = (y * 400 + x) * 4;
                    bool disc = (x - 200) * (x - 200) + (y - 150) * (y - 150) < 90 * 90;
                    image.Data[i] = image.Data[i + 1] = image.Data[i + 2] = disc ? (byte)40 : (byte)235;
                    if (disc) image.Data[i] = 180; // a blue disc
                    image.Data[i + 3] = 255;
                }
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = BackgroundRemover.Remove(image);
        Console.WriteLine($"background removal: {sw.ElapsedMilliseconds} ms for {image.Width} × {image.Height}");
        if (photo is null)
        {
            Assert.True(result.Data[(150 * 400 + 200) * 4 + 3] > 200, "the disc stays");
            Assert.True(result.Data[(10 * 400 + 10) * 4 + 3] < 50, "the corner goes");
        }
        string? output = Environment.GetEnvironmentVariable("PVIEWER_TEST_OUTPUT");
        if (output is not null)
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(ImageBridge.ToBitmapSource(result)));
            using var fs = File.Create(output);
            encoder.Save(fs);
        }
    }
}

public class BackgroundRemoverDownloadTests
{
    /// <summary>The real download (170 MB): only with PVIEWER_TEST_NETWORK=1.</summary>
    [Fact]
    public async Task ModelDownloadsAndPassesItsChecks()
    {
        if (Environment.GetEnvironmentVariable("PVIEWER_TEST_NETWORK") != "1") return;
        using var tmp = new TempFolder();
        string before = BackgroundRemover.ModelPath;
        try
        {
            BackgroundRemover.ModelPath = tmp.File(BackgroundRemover.ModelFile);
            await BackgroundRemover.DownloadModelAsync(null);
            Assert.True(BackgroundRemover.ModelReady);
        }
        finally
        {
            BackgroundRemover.ModelPath = before;
        }
    }
}
