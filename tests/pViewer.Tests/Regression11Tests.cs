using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Windows.Media;
using pViewer.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;

namespace pViewer.Tests;

/// <summary>
/// The ImageSharp 3.1 advisories of October 2026 (fixed only in 4.1.2, which pViewer cannot use):
/// what a hostile file can reach is guarded, the rest is shown to be out of reach. The NuGet
/// warnings are silenced in Directory.Build.props on the strength of these tests.
/// </summary>
public class Regression11Tests
{
    /// <summary>A BigTIFF header followed by its directories (entries of 20 bytes each).</summary>
    private static byte[] BigTiff(bool littleEndian, params (ulong Count, byte[] Entries, ulong Next)[] directories)
    {
        var file = new List<byte>();
        void U16(ushort v) => file.AddRange(littleEndian ? [(byte)v, (byte)(v >> 8)] : [(byte)(v >> 8), (byte)v]);
        void U64(ulong v)
        {
            var b = new byte[8];
            if (littleEndian) BinaryPrimitives.WriteUInt64LittleEndian(b, v);
            else BinaryPrimitives.WriteUInt64BigEndian(b, v);
            file.AddRange(b);
        }
        file.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(43); U16(8); U16(0); U64(16);
        foreach (var (count, entries, next) in directories)
        {
            U64(count);
            file.AddRange(entries);
            U64(next);
        }
        return file.ToArray();
    }

    private static byte[] Entry(ushort tag, ushort type, ulong count, ulong value)
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(b, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), type);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(4), count);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(12), value);
        return b;
    }

    /// <summary>A well-formed 2 × 2 BigTIFF, uncompressed RGB, all red.</summary>
    private static byte[] SmallBigTiff()
    {
        const ushort Short = 3, Long8 = 16;
        const int entries = 9;
        ulong pixels = 16 + 8 + entries * 20 + 8;
        byte[] directory =
        [
            .. Entry(256, Short, 1, 2),                     // ImageWidth
            .. Entry(257, Short, 1, 2),                     // ImageLength
            .. Entry(258, Short, 3, 8 | 8 << 16 | 8L << 32), // BitsPerSample 8, 8, 8
            .. Entry(259, Short, 1, 1),                     // Compression: none
            .. Entry(262, Short, 1, 2),                     // Photometric: RGB
            .. Entry(273, Long8, 1, pixels),                // StripOffsets
            .. Entry(277, Short, 1, 3),                     // SamplesPerPixel
            .. Entry(278, Short, 1, 2),                     // RowsPerStrip
            .. Entry(279, Long8, 1, 12),                    // StripByteCounts
        ];
        return [.. BigTiff(true, (entries, directory, 0)), .. Enumerable.Repeat(new byte[] { 255, 0, 0 }, 4).SelectMany(p => p)];
    }

    // GHSA-wmxv-xphr-5c9g: ImageSharp 3.1 counts up to the declared number of entries even when the
    // file holds none, so these few bytes kept the decoding thread busy for hours, or forever.
    [Theory]
    [InlineData(5_000_000_000UL, true)]
    [InlineData(5_000_000_000UL, false)]
    [InlineData(ulong.MaxValue, true)]
    public async Task BigTiffClaimingEntriesItDoesNotHoldIsRefusedAtOnce(ulong count, bool littleEndian)
    {
        // The count alone, and the same after a first, empty directory.
        foreach (var file in new[] { BigTiff(littleEndian, (count, [], 0))[..24], BigTiff(littleEndian, (0, [], 32), (count, [], 0))[..40] })
        {
            Assert.True(ImageDecoder.IsUnsafeBigTiff(file));
            var decode = Task.Run(() => Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(file, "big.tif", true)));
            Assert.Same(decode, await Task.WhenAny(decode, Task.Delay(5000)));
            await decode;
        }
    }

    [Fact]
    public void OrdinaryBigTiffStillOpens()
    {
        var file = SmallBigTiff();
        Assert.False(ImageDecoder.IsUnsafeBigTiff(file));
        var loaded = ImageDecoder.Decode(file, "big.tif", true);
        Assert.Equal((2, 2), (loaded.PixelWidth, loaded.PixelHeight));
        Assert.Equal(Colors.Red, Pixels.At(loaded.Bitmap, 1, 1));

        // Classic TIFF and other formats are not looked at.
        Assert.False(ImageDecoder.IsUnsafeBigTiff([0x49, 0x49, 42, 0, 8, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(ImageDecoder.IsUnsafeBigTiff(TestImages.Png(4, 4)));
        Assert.False(ImageDecoder.IsUnsafeBigTiff([]));
    }

    [Fact]
    public void BigTiffDirectoryWithThousandsOfEntriesIsRefused()
    {
        // All the entries are there, but the decoder compares each one with all the others: seconds
        // for tens of thousands of them, hours for a million.
        static byte[] Directory(int entries) =>
            BigTiff(true, ((ulong)entries, Enumerable.Range(0, entries).SelectMany(i => Entry((ushort)(40000 + i), 4, 1, (ulong)i)).ToArray(), 0));
        Assert.False(ImageDecoder.IsUnsafeBigTiff(Directory(1024)));
        Assert.True(ImageDecoder.IsUnsafeBigTiff(Directory(1025)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(Directory(60000), "big.tif", true));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The profile of GHSA-gwg2-r3hj-4w44: 200 bytes whose lookup table claims 15 input channels
    /// with 3 grid points each (14 million cells) and holds none of them.
    /// </summary>
    private static byte[] HostileProfile()
    {
        const int tag = 144, element = 168;
        var p = new byte[200];
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(at), v);
        void U16(int at, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(at), v);
        U32(0, (uint)p.Length);
        U32(8, 0x04000000);                     // version 4
        U32(12, 0x6D6E7472);                    // 'mntr': display device
        U32(16, 0x52474220);                    // 'RGB '
        U32(20, 0x58595A20);                    // 'XYZ '
        U32(36, 0x61637370);                    // 'acsp'
        U32(128, 1);                            // one tag
        U32(132, 0x41324230); U32(136, tag); U32(140, 56); // 'A2B0'
        U32(tag, 0x6D706574);                   // 'mpet'
        U32(tag + 12, 1); U32(tag + 16, 24); U32(tag + 20, 32);
        U32(element, 0x636C7574);               // 'clut'
        U16(element + 4, 15); U16(element + 6, 15);
        for (int i = 0; i < 15; i++) p[element + 8 + i] = 3;
        return p;
    }

    private static long AllocatedBy(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // GHSA-gwg2-r3hj-4w44: in ImageSharp 3.1 only IccProfile.Entries parses the tags of a profile.
    // pViewer never reads them: a profile is carried as bytes, both when opening and when saving.
    [Fact]
    public async Task HostileColorProfileIsCarriedButNeverParsed()
    {
        const long Megabyte = 1024 * 1024;
        var profile = HostileProfile();
        // The control: this is what parsing the profile costs. If this fails ImageSharp was fixed,
        // and the advisory can leave Directory.Build.props.
        Assert.True(AllocatedBy(() => _ = new IccProfile(profile).Entries.Length) > 50 * Megabyte);

        using var tmp = new TempFolder();
        var files = new List<byte[]>();
        foreach (var (encoder, frames) in new (SixLabors.ImageSharp.Formats.IImageEncoder, int)[]
        {
            (new PngEncoder(), 2),                                                      // animated PNG: read by ImageSharp
            (new PngEncoder(), 1),                                                      // WIC, then ImageSharp if it refuses the profile
            (new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }, 1),          // WIC, or ImageSharp without the WebP codec
            (new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder(), 1),
        })
        {
            using var img = new Image<Rgba32>(8, 8, new Rgba32(200, 30, 30));
            for (int i = 1; i < frames; i++)
            {
                using var frame = new Image<Rgba32>(8, 8, new Rgba32(0, 0, 255));
                img.Frames.AddFrame(frame.Frames.RootFrame);
            }
            img.Metadata.IccProfile = new IccProfile(profile);
            using var ms = new MemoryStream();
            img.Save(ms, encoder);
            files.Add(ms.ToArray());
        }
        foreach (var file in files)
        {
            // The profile is in the file as written, and ImageSharp itself opens the file without parsing it.
            using (var direct = SixLabors.ImageSharp.Image.Load<Bgra32>(file))
                Assert.Equal(profile, direct.Metadata.IccProfile?.ToByteArray());
            LoadedImage? loaded = null;
            Assert.True(AllocatedBy(() => loaded = ImageDecoder.Decode(file, "hostile", true)) < 8 * Megabyte);
            Assert.Equal(8, loaded!.PixelWidth);
        }
        Assert.NotNull(ImageDecoder.Decode(files[0], "hostile.png", true).Animation);

        // Saving as WebP is the one place where pViewer hands a profile to ImageSharp.
        string icc = tmp.File("hostile.icc"), output = tmp.File("out.webp");
        File.WriteAllBytes(icc, profile);
        var bitmap = ImageDecoder.Decode(TestImages.Png(8, 8), "a.png", true).Bitmap;
        long saving = await StaTask.Run(() => AllocatedBy(() => ImageSaver.Save(bitmap, output, 90, colorContexts: [new ColorContext(new Uri(icc))])));
        Assert.True(saving < 8 * Megabyte, $"saving allocated {saving / Megabyte} MB");
        using var saved = SixLabors.ImageSharp.Image.Load<Bgra32>(output);
        Assert.Equal(profile, saved.Metadata.IccProfile?.ToByteArray());
    }

    // GHSA-j3p4-wp97-rph4 (HistogramEqualization), GHSA-j9gm-c75j-xc9q and GHSA-jjfr-hcj7-qf5w (the
    // TIFF encoder with CCITT compression) and GHSA-gwg2-r3hj-4w44 (IccProfile.Entries) are out of
    // reach only as long as pViewer calls none of these. Whoever starts using one meets this test.
    [Fact]
    public void ImageSharpPartsWithOpenAdvisoriesAreNotUsed()
    {
        using var pe = new PEReader(File.OpenRead(typeof(ImageDecoder).Assembly.Location));
        var md = pe.GetMetadataReader();
        var types = md.TypeReferences.Select(h => md.GetTypeReference(h))
            .Select(t => $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}").ToList();
        var members = md.MemberReferences.Select(h => md.GetMemberReference(h))
            .Select(m => (Type: m.Parent.Kind == HandleKind.TypeReference ? md.GetString(md.GetTypeReference((TypeReferenceHandle)m.Parent).Name) : "",
                          Name: md.GetString(m.Name))).ToList();

        Assert.Contains(types, t => t == "SixLabors.ImageSharp.Image"); // the references are really being read
        Assert.Contains(members, m => m is { Type: "ImageExtensions", Name: "Save" });
        // TIFF is written by WIC (ImageSaver.Encode); nothing of ImageSharp's TIFF support is named.
        Assert.DoesNotContain(types, t => t.StartsWith("SixLabors.ImageSharp.Formats.Tiff", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Name.StartsWith("SaveAsTiff", StringComparison.Ordinal));
        Assert.DoesNotContain(types, t => t.Contains("HistogramEqualization", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Name.Contains("HistogramEqualization", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m is { Type: "IccProfile", Name: "get_Entries" });
    }
}
