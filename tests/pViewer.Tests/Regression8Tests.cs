using System.Windows;
using pViewer.Core;
using pViewer.Core.Sources;
using pViewer.Imaging;
using pViewer.Services;
using WColors = System.Windows.Media.Colors;

namespace pViewer.Tests;

/// <summary>Regressions for the eighth review pass.</summary>
public class Regression8Tests
{
    [Fact]
    public async Task RectangleLowerThanThePenIsDrawn()
    {
        var src = Pixels.Solid(60, 40, WColors.White);
        // 20 × 3 with a 5 px pen (an underline): the side bands would have a negative height.
        var flat = await StaTask.Run(() => ImageOps.DrawRectangle(src, new Int32Rect(10, 10, 20, 3), WColors.Black, 5, fill: false));
        Assert.Equal(0, Pixels.At(flat, 20, 11).R);
        Assert.Equal(255, Pixels.At(flat, 50, 30).R);
        var thick = await StaTask.Run(() => ImageOps.DrawRectangle(src, new Int32Rect(10, 10, 10, 10), WColors.Black, 30, fill: false));
        Assert.Equal(0, Pixels.At(thick, 15, 15).R);
    }

    [Fact]
    public void NeutralAdjustmentsLeaveTheImageAlone()
    {
        var src = Pixels.Solid(8, 8, WColors.Red);
        Assert.Same(src, ImageOps.BrightnessContrast(src, 0, 0));
        Assert.Same(src, ImageOps.HueSaturation(src, 0, 0));
        Assert.NotSame(src, ImageOps.BrightnessContrast(src, 10, 0));
    }

    [Fact]
    public void KeyMovedAwayAndBackRestoresTheDefaults()
    {
        Assert.True(Shortcut.TryParse("Space", out var space));
        var overrides = new Dictionary<string, List<string>>();
        Assert.Equal("Next", Hotkeys.Assign(overrides, "Last", space)?.Id);
        Assert.Equal("Last", Hotkeys.Assign(overrides, "Next", space)?.Id);
        // Same keys as the defaults, only in another order: nothing stored, default display.
        Assert.Empty(overrides);
        Assert.Equal(Hotkeys.DisplayText("Next", null, 2), Hotkeys.DisplayText("Next", overrides, 2));
    }

    [Fact]
    public void MisspeltKeysInSettingsKeepTheDefaults()
    {
        var clean = Hotkeys.Normalize(new() { ["Next"] = ["Rigth"], ["Previous"] = [] });
        Assert.NotNull(clean);
        Assert.False(clean.ContainsKey("Next"));
        Assert.Equal(Hotkeys.Find("Next")!.DefaultGestures, Hotkeys.GesturesOf("Next", clean));
        Assert.Empty(Hotkeys.GesturesOf("Previous", clean)); // an empty list still unbinds on purpose
    }

    [Theory]
    [InlineData("pages/Page:01.jpg", "Page_01.jpg")]
    [InlineData("p?.png", "p_.png")]
    [InlineData("dir\\cover.jpg", "cover.jpg")]
    [InlineData("odd. ", "odd")]
    [InlineData("...", "page")]
    public void ArchiveEntryNamesBecomeValidFileNames(string entry, string expected) =>
        Assert.Equal(expected, ImageFormats.SafeFileName(entry));

    [Fact]
    public async Task BatchWritesPagesWhoseNamesAreInvalidOnWindows()
    {
        using var tmp = new TempFolder();
        string zip = tmp.File("mac.cbz");
        TestImages.Zip(zip, ("Page:01.png", TestImages.Png(4, 4)), ("p?.png", TestImages.Png(4, 4)), ("ok.png", TestImages.Png(4, 4)));
        using var src = ArchiveSource.Open(zip);
        string output = Path.Combine(tmp.Path, "Out");
        var job = new BatchJob(BatchOperation.Convert, output, 0, 0, 0, ".png", 80, 10, AutoOrient: true);
        var result = await BatchProcessor.RunAsync(src, job, null, CancellationToken.None);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Written);
        Assert.True(File.Exists(Path.Combine(output, "Page_01.png")));
    }

    [Fact]
    public void ActualSizeIsNotKeptAsTheDefaultViewMode()
    {
        using var tmp = new TempFolder();
        string path = tmp.File("settings.json");
        File.WriteAllText(path, """{ "ViewMode": "ActualSize" }""");
        Assert.Equal(new AppSettings().ViewMode, SettingsStore.Load(path).ViewMode);
    }
}
