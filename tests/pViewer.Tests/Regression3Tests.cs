using System.Windows;
using System.Windows.Media;
using pViewer.Core;
using pViewer.Core.Sources;
using pViewer.Imaging;
using pViewer.Services;

namespace pViewer.Tests;

/// <summary>Regressions for the third review pass.</summary>
public class Regression3Tests
{
    [Fact]
    public void SwitchingToTwoPagesMidBookPairsFromTheCurrentPage()
    {
        var nav = new PageNavigator();
        nav.Reset(10);
        for (int i = 0; i < 5; i++) nav.Next();         // single page, reading page 5
        nav.Layout = PageLayout.Comic;
        Assert.Equal([5, 6], nav.VisibleIndices());
        nav.Previous();
        Assert.Equal([3, 4], nav.VisibleIndices());      // no page skipped going back
        nav.Previous();
        nav.Previous();
        Assert.Equal([0], nav.VisibleIndices());         // page 0 alone
    }

    [Fact]
    public void CoverPrefetchesTheViewThatFollows()
    {
        var nav = new PageNavigator { Layout = PageLayout.Comic };
        nav.Reset(6);
        nav.Shift(1);
        nav.Previous();                                  // cover [0]
        Assert.Equal([1, 2], nav.PrefetchIndices().Take(2));
    }

    [Fact]
    public async Task MissingFileFailsTheTaskInsteadOfThrowing()
    {
        using var tmp = new TempFolder();
        File.WriteAllBytes(tmp.File("a.png"), TestImages.Png(3, 3));
        using var src = FolderSource.Open(tmp.Path);
        File.Delete(tmp.File("a.png"));                  // deleted in Explorer meanwhile

        var read = src.ReadAsync(0);                     // must not throw here
        await Assert.ThrowsAnyAsync<IOException>(() => read);

        using var cache = new PageCache(src, true);
        var entry = cache.Get(0);                        // nor here
        await Assert.ThrowsAnyAsync<Exception>(() => entry.Image);
    }

    [Fact]
    public void DisposedCacheAnswersWithCancelledPages()
    {
        using var tmp = new TempFolder();
        File.WriteAllBytes(tmp.File("a.png"), TestImages.Png(3, 3));
        using var src = FolderSource.Open(tmp.Path);
        var cache = new PageCache(src, true);
        cache.Dispose();
        cache.Dispose();                                 // idempotent
        Assert.True(cache.Get(0).Image.IsCanceled);
    }

    [Fact]
    public void BlackAndWhiteKeepsTransparency()
    {
        var src = Pixels.Solid(4, 4, Color.FromArgb(0, 0, 0, 0));
        var bw = ImageOps.BlackWhite(src);
        Assert.Equal(0, Pixels.At(bw, 1, 1).A);
    }

    [Fact]
    public async Task JoinedPagesStayOnWholePixels()
    {
        var pair = await StaTask.Run(() => ImageOps.Compose(
            [Pixels.Solid(10, 20, Colors.Red), Pixels.Solid(10, 19, Color.FromRgb(0, 0, 255))], Colors.White));
        Assert.Equal(Color.FromRgb(0, 0, 255), Pixels.At(pair, 15, 0));   // not blended with the background
        Assert.Equal(Color.FromRgb(0, 0, 255), Pixels.At(pair, 15, 18));
    }

    [Fact]
    public async Task RectangleEdgesAreCrisp()
    {
        var src = Pixels.Solid(20, 20, Colors.White);
        var r = await StaTask.Run(() => ImageOps.DrawRectangle(src, new Int32Rect(5, 5, 10, 10), Colors.Black, 5, fill: false));
        for (int x = 0; x < 20; x++)
        {
            byte v = Pixels.At(r, x, 10).R;
            Assert.True(v == 0 || v == 255, $"pixel {x} is {v}: anti-aliased edge");
        }
    }

    [Fact]
    public void GifKeepsTransparencyAndLongNamesSave()
    {
        using var tmp = new TempFolder();
        string name = new string('n', 200) + ".gif";      // + a long temp suffix used to exceed 255
        string path = tmp.File(name);
        ImageSaver.Save(Pixels.Solid(6, 6, Color.FromArgb(0, 255, 0, 0)), path, 90);
        var loaded = ImageDecoder.Decode(File.ReadAllBytes(path), name, true);
        Assert.Equal(0, Pixels.At(loaded.Bitmap, 2, 2).A);
    }
}
