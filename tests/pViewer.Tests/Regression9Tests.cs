using System.IO.Compression;
using System.Text;
using pViewer.Core.Sources;
using pViewer.Imaging;
using pViewer.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace pViewer.Tests;

/// <summary>Regressions for the ninth review pass.</summary>
public class Regression9Tests
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

    [Theory]
    [InlineData("iTXt")]
    [InlineData("zTXt")]
    [InlineData("iCCP")]
    public async Task PngChunkInflatingToHugeSizeIsLeftOut(string type)
    {
        var png = TestImages.Png(16, 16);
        var z = Zlib(40);
        byte[] payload = type == "iTXt"
            ? [.. "Comment\0"u8.ToArray(), 1, 0, 0, 0, .. z]
            : [.. "Comment\0"u8.ToArray(), 0, .. z];
        var bomb = png.Take(33).Concat(Chunk(type, payload)).Concat(png.Skip(33)).ToArray();

        var clean = ImageDecoder.WithoutPngBombs(bomb);
        Assert.Equal(png, clean); // exactly the chunk removed
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var loaded = ImageDecoder.Decode(bomb, "bomb.png", true);
        Assert.Equal(16, loaded.PixelWidth);
        await MetadataService.ReadAsync(bomb);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
        // An ordinary PNG is returned as it is.
        Assert.Same(png, ImageDecoder.WithoutPngBombs(png));
    }

    [Fact]
    public async Task LosslessWebpStaysLosslessWhenSaved()
    {
        using var tmp = new TempFolder();
        var rnd = new Random(3);
        using var img = new Image<Rgba32>(32, 32);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                img[x, y] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255);
        string lossless = tmp.File("lossless.webp"), lossy = tmp.File("lossy.webp");
        img.Save(lossless, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        img.Save(lossy, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy });

        var loaded = ImageDecoder.Decode(File.ReadAllBytes(lossless), "lossless.webp", true);
        Assert.True(loaded.LosslessWebp);
        Assert.False(ImageDecoder.Decode(File.ReadAllBytes(lossy), "lossy.webp", true).LosslessWebp);
        Assert.False(ImageDecoder.Decode(TestImages.Png(4, 4), "a.png", true).LosslessWebp);

        string output = tmp.File("out.webp");
        await StaTask.Run(() =>
        {
            ImageSaver.Save(ImageOps.Rotate(loaded.Bitmap, 180), output, 80, losslessWebp: loaded.LosslessWebp);
            return true;
        });
        Assert.True(ImageDecoder.IsLosslessWebp(File.ReadAllBytes(output)));
        using var back = SixLabors.ImageSharp.Image.Load<Rgba32>(output);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                Assert.Equal(img[x, y], back[31 - x, 31 - y]);
    }

    [Fact]
    public async Task PageThatCouldNotBeReadIsTriedAgain()
    {
        using var tmp = new TempFolder();
        File.WriteAllBytes(tmp.File("1.png"), TestImages.Png(4, 4));
        File.WriteAllBytes(tmp.File("2.png"), TestImages.Png(6, 6));
        using var source = FolderSource.Open(tmp.Path);
        using var cache = new PageCache(source, true);
        using (new FileStream(tmp.File("2.png"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Prefetched while another program still has it open (e.g. being copied).
            await Assert.ThrowsAnyAsync<IOException>(() => cache.Get(1).Image);
        }
        var image = await cache.Get(1).Image;
        Assert.Equal(6, image.PixelWidth);
    }
}
