using System.IO.Compression;
using System.Text;
using System.Text.Json;
using pViewer.Imaging;
using pViewer.Services;

namespace pViewer.Tests;

/// <summary>Regressions for the tenth review pass.</summary>
public class Regression10Tests
{
    private static uint Crc(byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in b)
        {
            c ^= x;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        return [.. BigEndian((uint)data.Length), .. typed, .. BigEndian(Crc(typed))];
    }

    private static byte[] Zlib(int megabytes)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var block = new byte[1 << 20];
            for (int i = 0; i < megabytes; i++) z.Write(block);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task ManyChunksJustUnderTheLimitShareOneBudget()
    {
        var png = TestImages.Png(16, 16);
        // Twelve 7 MB text chunks: each under the 8 MB limit, 84 MB together.
        var chunk = Chunk("zTXt", [.. "Comment\0"u8.ToArray(), 0, .. Zlib(7)]);
        var bomb = png.Take(33).Concat(Enumerable.Repeat(chunk, 12).SelectMany(c => c)).Concat(png.Skip(33)).ToArray();

        var clean = ImageDecoder.WithoutPngBombs(bomb);
        Assert.True(clean.Length < bomb.Length);
        Assert.True(clean.Length - png.Length <= 4 * chunk.Length); // at most 32 MB of text left
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await MetadataService.ReadAsync(bomb);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
        // The size shown is the one of the file, not of what was decoded.
        Assert.Equal(bomb.LongLength, ImageDecoder.Decode(bomb, "bomb.png", true).FileSize);
    }

    [Fact]
    public void LockedSettingsAreNeverOverwrittenWithTheDefaults()
    {
        using var tmp = new TempFolder();
        string path = tmp.File("settings.json");
        SettingsStore.Save(new AppSettings { JpegQuality = 55, LastFolder = @"D:\Comics" }, path);
        string before = File.ReadAllText(path);
        AppSettings loaded;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            loaded = SettingsStore.Load(path);
        Assert.True(loaded.NotLoaded);
        SettingsStore.Save(loaded, path); // what the app does on exit
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(55, SettingsStore.Load(path).JpegQuality);
        Assert.False(File.Exists(path + ".bad"));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("\"big\"")]
    [InlineData("0")]
    [InlineData("99999999999999999999")]
    public void ReleaseWithAnOddInstallerSizeOffersNothing(string size)
    {
        var json = JsonDocument.Parse($$"""
            { "tag_name": "v9.0.0", "assets": [ { "name": "pViewer-Setup-9.0.0-Light.exe", "size": {{size}},
              "browser_download_url": "https://github.com/FirekeeperPhate/pViewer/releases/download/v9.0.0/pViewer-Setup-9.0.0-Light.exe" } ] }
            """);
        Assert.Null(UpdateService.ParseRelease(json.RootElement, new Version(2, 0, 8), fullEdition: false));
    }
}
