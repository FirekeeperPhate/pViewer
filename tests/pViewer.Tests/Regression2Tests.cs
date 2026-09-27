using System.Windows.Media;
using System.Windows.Media.Imaging;
using pViewer.Core;
using pViewer.Core.Sources;
using pViewer.Imaging;
using pViewer.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace pViewer.Tests;

/// <summary>Regressions for the second review pass.</summary>
public class Regression2Tests
{
    [Fact]
    public void GoingBackAfterRealigningKeepsTheNewPairs()
    {
        var nav = new PageNavigator { Layout = PageLayout.Comic };
        nav.Reset(6);
        Assert.True(nav.Shift(1));                       // F12: pairs now start on odd pages
        Assert.Equal([1, 2], nav.VisibleIndices());
        nav.Previous();
        Assert.Equal([0], nav.VisibleIndices());         // page 0 alone, like a cover
        nav.Next();
        Assert.Equal([1, 2], nav.VisibleIndices());      // not [2,3]: the alignment is kept
        nav.Next();
        Assert.Equal([3, 4], nav.VisibleIndices());
        nav.Next();
        Assert.Equal([5], nav.VisibleIndices());
    }

    [Fact]
    public void MultiPageTiffShowsTheFirstPageAndReportsThePageCount()
    {
        var encoder = new TiffBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Pixels.Solid(10, 8, Colors.Red)));
        encoder.Frames.Add(BitmapFrame.Create(Pixels.Solid(40, 30, Colors.Blue))); // larger page 2
        using var ms = new MemoryStream();
        encoder.Save(ms);

        var loaded = ImageDecoder.Decode(ms.ToArray(), "doc.tif", true);
        Assert.Equal(2, loaded.PageCount);
        Assert.Equal(10, loaded.PixelWidth); // page 1, not the largest frame
    }

    [Fact]
    public void DecodeRecordsWhetherPixelsWereAutoRotated()
    {
        var jpeg = TestImages.JpegWithOrientation(20, 10, 6);
        Assert.True(ImageDecoder.Decode(jpeg, "a.jpg", autoOrient: true).AutoOriented);
        Assert.False(ImageDecoder.Decode(jpeg, "a.jpg", autoOrient: false).AutoOriented);
    }

    [Fact]
    public void MetadataOfAnUnknownFormatIsJustEmpty()
    {
        using var img = new Image<Rgba32>(4, 4);
        using var ms = new MemoryStream();
        img.SaveAsQoi(ms);
        Assert.Empty(MetadataService.Read(ms.ToArray()));
    }

    [Fact]
    public async Task InvalidateDoesNotCancelAPageBeingAwaited()
    {
        using var tmp = new TempFolder();
        File.WriteAllBytes(tmp.File("a.png"), TestImages.Png(9, 9));
        using var src = FolderSource.Open(tmp.Path);
        using var cache = new PageCache(src, true);
        var entry = cache.Get(0);
        cache.Invalidate(0);                              // e.g. a save of this file just finished
        var image = await entry.Image;                    // the current request still gets its page
        Assert.Equal(9, image.PixelWidth);
        Assert.NotSame(entry, cache.Get(0));              // and the next request reads it again
    }

    [Fact]
    public void TrimCancelsPagesThatAreNoLongerNeeded()
    {
        using var tmp = new TempFolder();
        for (int i = 0; i < 3; i++) File.WriteAllBytes(tmp.File($"{i}.png"), TestImages.Png(5, 5));
        using var src = FolderSource.Open(tmp.Path);
        using var cache = new PageCache(src, true);
        var passed = cache.Get(0);
        cache.Trim([2]);
        Assert.True(passed.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void CorruptSettingsAreKeptAsBackup()
    {
        using var tmp = new TempFolder();
        string path = tmp.File("settings.json");
        File.WriteAllText(path, "{ broken");
        SettingsStore.Load(path);
        Assert.Equal("{ broken", File.ReadAllText(path + ".bad"));
    }
}
