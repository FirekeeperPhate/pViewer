using pViewer.Core;
using pViewer.Core.Sources;
using pViewer.Imaging;
using pViewer.Services;
using pViewer.ViewModels;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace pViewer.Tests;

/// <summary>Regressions for the bugs found in the review of 2026-09-27.</summary>
public class RegressionTests
{
    [Fact]
    public void LastPairKeepsTheForwardAlignment()
    {
        var nav = new PageNavigator { Layout = PageLayout.Manga };
        nav.Reset(5);
        nav.Last();
        Assert.Equal([4], nav.VisibleIndices()); // same view reached going forward, not [3,4]
        nav.Previous();
        Assert.Equal([3, 2], nav.VisibleIndices());

        nav.First();
        Assert.Equal(NavigationResult.Wrapped, nav.Previous()); // wrap from the first pair
        Assert.Equal(4, nav.Position);

        nav.Reset(5, 1); // realigned with F12: pairs start on odd pages
        nav.Last();
        Assert.Equal([4, 3], nav.VisibleIndices());
    }

    [Fact]
    public void BatchRenameRollsBackFilesAlreadyRenamed()
    {
        using var tmp = new TempFolder();
        File.WriteAllText(tmp.File("a.jpg"), "A");
        File.WriteAllText(tmp.File("b.jpg"), "B");
        // A folder with the second final name: the check for existing files does not see it, so
        // the second pass fails after "a.jpg" has already become "photo-1.jpg".
        Directory.CreateDirectory(tmp.File("photo-2.jpg"));

        Assert.ThrowsAny<IOException>(() =>
            BatchRenamer.Rename([tmp.File("a.jpg"), tmp.File("b.jpg")], new BatchRenameOptions("photo", 1, 1)));

        Assert.Equal("A", File.ReadAllText(tmp.File("a.jpg")));
        Assert.Equal("B", File.ReadAllText(tmp.File("b.jpg")));
        Assert.False(File.Exists(tmp.File("photo-1.jpg")));
        Assert.Empty(Directory.GetFiles(tmp.Path, ".pvren_*"));
    }

    [Fact]
    public async Task BatchGivesUniqueNamesToCollidingPages()
    {
        using var tmp = new TempFolder();
        string zip = tmp.File("book.cbz");
        TestImages.Zip(zip, ("ch1/001.png", TestImages.Png(4, 4)), ("ch2/001.png", TestImages.Png(6, 6)));
        using var src = ArchiveSource.Open(zip);
        string output = Path.Combine(tmp.Path, "out");
        var job = new BatchJob(BatchOperation.Grayscale, output, 0, 0, 0, null, 80, 10, true);

        var result = await BatchProcessor.RunAsync(src, job, null, CancellationToken.None);

        Assert.Equal(2, result.Written);
        Assert.Equal(4, ImageDecoder.Decode(File.ReadAllBytes(Path.Combine(output, "001.png")), "001.png", true).PixelWidth);
        Assert.Equal(6, ImageDecoder.Decode(File.ReadAllBytes(Path.Combine(output, "001 (2).png")), "x.png", true).PixelWidth);

        var again = await BatchProcessor.RunAsync(src, job, null, CancellationToken.None);
        Assert.Equal(0, again.Written); // a second run never overwrites
        Assert.Equal(2, again.Skipped);
    }

    [Fact]
    public void DamagedNestedArchiveIsSkipped()
    {
        using var tmp = new TempFolder();
        var inner = TestImages.ZipBytes(("x.png", TestImages.Png(3, 3)), ("y.png", TestImages.Png(3, 3)));
        var truncated = inner[..(inner.Length / 2)];
        string zip = tmp.File("comic.cbz");
        TestImages.Zip(zip, ("01.png", TestImages.Png(5, 5)), ("broken.zip", truncated), ("garbage.cbz", [1, 2, 3]));

        using var src = ArchiveSource.Open(zip);
        Assert.Contains(src.Pages, p => p.Name == "01.png");
    }

    [Fact]
    public void OrientationIsKeptWhenPixelsWereNotRotated()
    {
        using var tmp = new TempFolder();
        var loaded = ImageDecoder.Decode(TestImages.JpegWithOrientation(40, 20, 6), "a.jpg", autoOrient: false);
        string path = tmp.File("kept.jpg");
        ImageSaver.Save(loaded.Bitmap, path, 90, loaded.JpegMetadata, resetOrientation: false);

        using var reread = SixLabors.ImageSharp.Image.Load(path);
        Assert.Equal(40, reread.Width); // pixels still sideways…
        Assert.True(reread.Metadata.ExifProfile!.TryGetValue(ExifTag.Orientation, out var o));
        Assert.Equal((ushort)6, o!.Value); // …and still tagged as such, so viewers rotate them
    }

    [Fact]
    public void SavingOverAReadOnlyFileFailsCleanly()
    {
        using var tmp = new TempFolder();
        string path = tmp.File("ro.png");
        File.WriteAllBytes(path, TestImages.Png(3, 3));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var ex = Assert.Throws<IOException>(() => ImageSaver.Save(Pixels.Solid(5, 5, System.Windows.Media.Colors.Red), path, 90));
            Assert.Contains("read-only", ex.Message);
            Assert.Single(Directory.GetFiles(tmp.Path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
