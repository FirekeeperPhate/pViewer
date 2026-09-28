using System.Windows.Media;
using pViewer.Core;
using pViewer.Core.Sources;
using pViewer.Imaging;
using pViewer.Services;
using pViewer.ViewModels;

namespace pViewer.Tests;

/// <summary>Regressions for the seventh review pass.</summary>
public class Regression7Tests
{
    [Fact]
    public void TinyFileClaimingAHugeImageIsRejectedWithoutAllocatingIt()
    {
        // TGA header: RLE true color, 30000 × 30000, 32 bpp; then a few RLE packets.
        var tga = new byte[18 + 64];
        tga[2] = 10;
        BitConverter.GetBytes((ushort)30000).CopyTo(tga, 12);
        BitConverter.GetBytes((ushort)30000).CopyTo(tga, 14);
        tga[16] = 32;
        tga[17] = 8;
        for (int i = 18; i < tga.Length; i += 5) tga[i] = 0xFF;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(tga, "bomb.tga", true));
        Assert.True(GC.GetTotalAllocatedBytes(precise: true) - before < 64L * 1024 * 1024);
    }

    [Fact]
    public void EndlessExifChainIsDetectedBeforeTheLibraryRecursesIntoIt()
    {
        // Little-endian TIFF with 3000 empty IFDs linked one after the other.
        int count = 3000;
        var tiff = new byte[8 + count * 6];
        tiff[0] = (byte)'I'; tiff[1] = (byte)'I'; tiff[2] = 42;
        BitConverter.GetBytes(8u).CopyTo(tiff, 4);
        for (int i = 0; i < count; i++)
        {
            int at = 8 + i * 6; // 2 bytes entry count (0) + 4 bytes next offset
            uint next = i == count - 1 ? 0u : (uint)(at + 6);
            BitConverter.GetBytes(next).CopyTo(tiff, at + 2);
        }
        Assert.True(MetadataService.HasEndlessIfdChain(tiff));
        Assert.Empty(MetadataService.Read(tiff));
        Assert.False(MetadataService.HasEndlessIfdChain(TestImages.JpegWithOrientation(8, 8, 6)));
    }

    [Fact]
    public void HandEditedSettingsAreBroughtBackIntoRange()
    {
        using var tmp = new TempFolder();
        string path = tmp.File("settings.json");
        File.WriteAllText(path, """
            { "Theme": -3, "ViewMode": 99, "JpegQuality": 5000, "BorderThickness": 2000000000,
              "TextFontSize": 1e6, "SlideshowSeconds": 1e7, "TextFontFamily": null, "CropColor": null,
              "Window": { "Left": 0, "Top": 0, "Width": 1e400, "Height": 500 } }
            """);
        var s = SettingsStore.Load(path);
        var d = new AppSettings();
        Assert.Equal(d.Theme, s.Theme);
        Assert.Equal(d.ViewMode, s.ViewMode);
        Assert.Equal(100, s.JpegQuality);
        Assert.Equal(2000, s.BorderThickness);
        Assert.Equal(d.TextFontSize, s.TextFontSize);
        Assert.Equal(d.SlideshowSeconds, s.SlideshowSeconds);
        Assert.Equal(d.TextFontFamily, s.TextFontFamily);
        Assert.Equal(d.CropColor, s.CropColor);
        Assert.Null(s.Window);
        Assert.False(File.Exists(path + ".bad")); // readable, just out of range
    }

    [Fact]
    public void ColorsThatThrowOtherExceptionsFallBack() =>
        Assert.Equal(Colors.Red, MainViewModel.ParseColor("sc#1", Colors.Red));

    [Fact]
    public void LaterPartsOfAMultiPartRarAreNotVolumes()
    {
        using var tmp = new TempFolder();
        foreach (var name in new[] { "Vol1.part1.rar", "Vol1.part2.rar", "Vol1.part10.rar", "Vol2.part01.rar", "Vol2.part02.rar", "Extra.cbz" })
            File.WriteAllBytes(tmp.File(name), [0]);
        var siblings = ArchiveSource.Siblings(tmp.File("Vol1.part1.rar")).Select(Path.GetFileName);
        Assert.Equal(["Extra.cbz", "Vol1.part1.rar", "Vol2.part01.rar"], siblings);
    }

    [Fact]
    public void SavingOverAFileKeepsItsAttributesAndCreationDate()
    {
        using var tmp = new TempFolder();
        string path = tmp.File("hidden.png");
        File.WriteAllBytes(path, TestImages.Png(4, 4));
        var created = new DateTime(2015, 6, 1, 12, 0, 0, DateTimeKind.Local);
        File.SetCreationTime(path, created);
        File.SetAttributes(path, FileAttributes.Hidden);
        var bitmap = ImageDecoder.Decode(File.ReadAllBytes(path), "hidden.png", true).Bitmap;
        StaTask.Run(() => { ImageSaver.Save(bitmap, path, 90); return true; }).GetAwaiter().GetResult();
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        Assert.Equal(created, File.GetCreationTime(path));
        File.SetAttributes(path, FileAttributes.Normal);
    }

    [Fact]
    public void PairingCanBeRestoredForAVolume()
    {
        var nav = new PageNavigator { Layout = PageLayout.Manga };
        nav.Reset(5, 0, alignment: 1);   // F12 had been used in this volume
        Assert.Equal([0], nav.VisibleIndices());
        Assert.Equal(3, nav.LastPosition()); // [3,4], as the pairs were read
    }
}
