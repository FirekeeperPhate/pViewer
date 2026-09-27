using pViewer.Core;
using pViewer.Core.Sources;
using pViewer.Services;
using pViewer.ViewModels;

namespace pViewer.Tests;

public class NaturalComparerTests
{
    [Fact]
    public void SortsNumbersLikeExplorer()
    {
        var list = new List<string> { "p10.jpg", "p2.jpg", "p1.jpg", "P3.jpg" };
        list.Sort(NaturalComparer.Instance);
        Assert.Equal(["p1.jpg", "p2.jpg", "P3.jpg", "p10.jpg"], list);
    }
}

public class PageNavigatorTests
{
    [Fact]
    public void SingleWrapsAround()
    {
        var nav = new PageNavigator();
        nav.Reset(3, 2);
        Assert.Equal(NavigationResult.Wrapped, nav.Next());
        Assert.Equal(0, nav.Position);
        Assert.Equal(NavigationResult.Wrapped, nav.Previous());
        Assert.Equal(2, nav.Position);
    }

    [Fact]
    public void MangaShowsPairRightToLeft()
    {
        var nav = new PageNavigator { Layout = PageLayout.Manga };
        nav.Reset(5);
        Assert.Equal([1, 0], nav.VisibleIndices());
        nav.Next();
        Assert.Equal(2, nav.Position);
        Assert.Equal([3, 2], nav.VisibleIndices());
        nav.Next();
        Assert.Equal([4], nav.VisibleIndices()); // numero dispari: l'ultima pagina è da sola
    }

    [Fact]
    public void ComicShowsPairLeftToRight()
    {
        var nav = new PageNavigator { Layout = PageLayout.Comic };
        nav.Reset(4);
        Assert.Equal([0, 1], nav.VisibleIndices());
        Assert.Equal([2, 3], nav.PrefetchIndices());
    }

    [Fact]
    public void AsksForNextAndPreviousContainer()
    {
        var nav = new PageNavigator { HasNextContainer = true, HasPreviousContainer = true };
        nav.Reset(2, 1);
        Assert.Equal(NavigationResult.NextContainer, nav.Next());
        Assert.Equal(1, nav.Position);
        nav.First();
        Assert.Equal(NavigationResult.PreviousContainer, nav.Previous());
    }

    [Fact]
    public void LastPositionShowsLastPair()
    {
        var nav = new PageNavigator { Layout = PageLayout.Comic };
        nav.Reset(6);
        nav.Last();
        Assert.Equal([4, 5], nav.VisibleIndices());
        Assert.True(nav.Shift(-1));
        Assert.Equal([3, 4], nav.VisibleIndices());
    }

    [Fact]
    public void SinglePageListDoesNotMove()
    {
        var nav = new PageNavigator();
        nav.Reset(1);
        Assert.Equal(NavigationResult.NoChange, nav.Next());
        Assert.Equal(NavigationResult.NoChange, nav.Previous());
    }
}

public class FolderSourceTests
{
    [Fact]
    public void ListsOnlyImagesInNaturalOrder()
    {
        using var tmp = new TempFolder();
        foreach (var n in new[] { "a10.png", "a2.png", "a1.png" }) File.WriteAllBytes(tmp.File(n), TestImages.Png(4, 4));
        File.WriteAllText(tmp.File("note.txt"), "x");
        File.WriteAllBytes(tmp.File("hidden.png"), TestImages.Png(4, 4));
        File.SetAttributes(tmp.File("hidden.png"), FileAttributes.Hidden);

        using var src = FolderSource.Open(tmp.Path);
        Assert.Equal(["a1.png", "a2.png", "a10.png"], src.Pages.Select(p => p.Name));
        Assert.Equal(2, src.IndexOf(tmp.File("A10.PNG")));
    }
}

public class ArchiveSourceTests
{
    [Fact]
    public async Task ReadsSortedPagesSkippingJunkAndFlatteningNestedArchives()
    {
        using var tmp = new TempFolder();
        string zip = tmp.File("volume 1.cbz");
        var inner = TestImages.ZipBytes(("q1.png", TestImages.Png(3, 3)));
        TestImages.Zip(zip,
            ("pages/", null),
            ("pages/p10.png", TestImages.Png(10, 5)),
            ("pages/p2.png", TestImages.Png(2, 5)),
            ("pages/p1.png", TestImages.Png(1, 5)),
            ("__MACOSX/pages/._p1.png", new byte[] { 1, 2, 3 }),
            ("readme.txt", "ciao"u8.ToArray()),
            ("extra.zip", inner));

        using var src = ArchiveSource.Open(zip);
        Assert.Equal(["extra.zip/q1.png", "pages/p1.png", "pages/p2.png", "pages/p10.png"], src.Pages.Select(p => p.Name));
        Assert.All(src.Pages, p => Assert.Null(p.FilePath));

        byte[] data = await src.ReadAsync(3);
        var img = Imaging.ImageDecoder.Decode(data, "p10.png", autoOrient: true);
        Assert.Equal(10, img.PixelWidth);
        byte[] nested = await src.ReadAsync(0);
        Assert.Equal(3, Imaging.ImageDecoder.Decode(nested, "q1.png", true).PixelWidth);
    }

    [Fact]
    public void ArchiveWithoutImagesFails()
    {
        using var tmp = new TempFolder();
        string zip = tmp.File("empty.zip");
        TestImages.Zip(zip, ("a.txt", "x"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => ArchiveSource.Open(zip));
    }

    [Fact]
    public void SiblingsAreArchivesInNaturalOrder()
    {
        using var tmp = new TempFolder();
        foreach (var n in new[] { "vol 10.cbz", "vol 2.cbr", "vol 1.zip", "cover.jpg" }) File.WriteAllBytes(tmp.File(n), [0]);
        var siblings = ArchiveSource.Siblings(tmp.File("vol 2.cbr")).Select(Path.GetFileName);
        Assert.Equal(["vol 1.zip", "vol 2.cbr", "vol 10.cbz"], siblings);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void RoundTrips()
    {
        using var tmp = new TempFolder();
        string path = tmp.File("settings.json");
        var s = new AppSettings { ViewMode = ViewMode.FitWidth, ArchiveLayout = PageLayout.Manga, JpegQuality = 77, Window = new WindowPlacement { Width = 800, Height = 600 } };
        SettingsStore.Save(s, path);
        Assert.Contains("\"FitWidth\"", File.ReadAllText(path));
        var loaded = SettingsStore.Load(path);
        Assert.Equal(ViewMode.FitWidth, loaded.ViewMode);
        Assert.Equal(PageLayout.Manga, loaded.ArchiveLayout);
        Assert.Equal(77, loaded.JpegQuality);
        Assert.Equal(800, loaded.Window!.Width);
    }

    [Fact]
    public void CorruptFileGivesDefaults()
    {
        using var tmp = new TempFolder();
        string path = tmp.File("settings.json");
        File.WriteAllText(path, "{ non è json");
        Assert.Equal(90, SettingsStore.Load(path).JpegQuality);
    }
}

public class BatchRenamerTests
{
    [Fact]
    public void RenamesInOrderEvenWhenNamesSwap()
    {
        using var tmp = new TempFolder();
        // "foto-2" deve diventare "foto-1" e viceversa: serve il passaggio per nomi temporanei.
        File.WriteAllText(tmp.File("foto-2.jpg"), "A");
        File.WriteAllText(tmp.File("foto-1.jpg"), "B");
        var files = new List<string> { tmp.File("foto-2.jpg"), tmp.File("foto-1.jpg") };

        var map = BatchRenamer.Rename(files, new BatchRenameOptions("foto", 1, 1));

        Assert.Equal("A", File.ReadAllText(tmp.File("foto-1.jpg")));
        Assert.Equal("B", File.ReadAllText(tmp.File("foto-2.jpg")));
        Assert.Equal(tmp.File("foto-1.jpg"), map[tmp.File("foto-2.jpg")]);
        Assert.Equal(2, Directory.GetFiles(tmp.Path).Length);
    }

    [Fact]
    public void RefusesToOverwriteFilesOutsideTheList()
    {
        using var tmp = new TempFolder();
        File.WriteAllText(tmp.File("x.png"), "A");
        File.WriteAllText(tmp.File("img-01.png"), "estraneo");
        Assert.Throws<IOException>(() => BatchRenamer.Rename([tmp.File("x.png")], new BatchRenameOptions("img", 1, 2)));
        Assert.Equal("A", File.ReadAllText(tmp.File("x.png")));
    }
}
