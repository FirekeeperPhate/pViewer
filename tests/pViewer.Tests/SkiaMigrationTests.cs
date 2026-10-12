using System.Windows.Media.Imaging;
using pViewer.Imaging;
using SixLabors.ImageSharp;
using IImageEncoder = SixLabors.ImageSharp.Formats.IImageEncoder;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Pbm;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Qoi;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using WColor = System.Windows.Media.Color;

namespace pViewer.Tests;

/// <summary>
/// pViewer moved from ImageSharp to Skia plus its own small decoders, resampler, color filters,
/// animated PNG reader and GIF writer. ImageSharp stays in the tests as the second opinion: what
/// pViewer reads, writes and computes is compared with what ImageSharp makes of the same data.
/// </summary>
public class SkiaMigrationTests
{
    private static Image<Rgba32> Noise(int width, int height, int seed, bool alpha = false, int colors = 0)
    {
        var rnd = new Random(seed);
        Rgba32[]? palette = colors > 0
            ? [.. Enumerable.Range(0, colors).Select(_ => new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255))]
            : null;
        var img = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                img[x, y] = palette is not null ? palette[rnd.Next(colors)]
                    : new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), alpha ? (byte)rnd.Next(256) : (byte)255);
        return img;
    }

    /// <summary>A soft picture (gradients and a few shapes): what filters and resizing are judged on.</summary>
    private static Image<Rgba32> Scene(int width, int height, bool alpha = false)
    {
        var img = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool disc = (x - width / 3) * (x - width / 3) + (y - height / 2) * (y - height / 2) < height * height / 9;
                byte a = alpha ? (byte)(x * 255 / width) : (byte)255;
                img[x, y] = disc ? new Rgba32(20, 40, 200, a) : new Rgba32((byte)(x * 255 / width), (byte)(y * 255 / height), 90, a);
            }
        return img;
    }

    /// <summary>The same random pictures and damage on every run (string hashes change from run to run).</summary>
    private static int Seed(string text) => text.Aggregate(17, (h, c) => unchecked(h * 31 + c));

    private static byte[] Encode(Image img, IImageEncoder encoder)
    {
        using var ms = new MemoryStream();
        img.Save(ms, encoder);
        return ms.ToArray();
    }

    private static byte[] Bgra(Image<Rgba32> img)
    {
        using var bgra = img.CloneAs<Bgra32>();
        var bytes = new byte[img.Width * img.Height * 4];
        bgra.CopyPixelDataTo(bytes);
        return bytes;
    }

    private static byte[] Bgra(BitmapSource bitmap) => ImageBridge.ToPixels(bitmap).Data;

    private static BitmapSource Bitmap(Image<Rgba32> img) => ImageBridge.ToBitmapSource(new PixelBuffer(Bgra(img), img.Width, img.Height));

    /// <summary>Largest and mean difference between two pictures; a fully transparent pixel has no color to compare.</summary>
    private static (int Max, double Mean) Difference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int max = 0;
        long sum = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i + 3] == 0 && b[i + 3] == 0) continue;
            for (int k = 0; k < 4; k++)
            {
                int d = Math.Abs(a[i + k] - b[i + k]);
                max = Math.Max(max, d);
                sum += d;
            }
        }
        return (max, (double)sum / a.Length);
    }

    // ---- The three decoders written for pViewer ----

    public static TheoryData<string, string> SmallFormats()
    {
        var data = new TheoryData<string, string>();
        foreach (string kind in new[] { "tga24", "tga32", "tga24rle", "tga32rle", "tga16", "tga8gray", "qoi3", "qoi4",
                                        "pbmplain", "pbmbinary", "pgmplain", "pgmbinary", "ppmplain", "ppmbinary", "ppm16" })
            data.Add(kind, kind.StartsWith("tga") ? "x.tga" : kind.StartsWith("qoi") ? "x.qoi" : "x.pbm");
        return data;
    }

    private static byte[] SmallFormatFile(string kind, Image<Rgba32> img) => kind switch
    {
        "tga24" => Encode(img, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel24, Compression = TgaCompression.None }),
        "tga32" => Encode(img, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel32, Compression = TgaCompression.None }),
        "tga24rle" => Encode(img, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel24, Compression = TgaCompression.RunLength }),
        "tga32rle" => Encode(img, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel32, Compression = TgaCompression.RunLength }),
        "tga16" => Encode(img, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel16, Compression = TgaCompression.RunLength }),
        "tga8gray" => Encode(img, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel8, Compression = TgaCompression.None }),
        "qoi3" => Encode(img, new QoiEncoder { Channels = QoiChannels.Rgb }),
        "qoi4" => Encode(img, new QoiEncoder { Channels = QoiChannels.Rgba }),
        "pbmplain" => Encode(img, new PbmEncoder { ColorType = PbmColorType.BlackAndWhite, Encoding = PbmEncoding.Plain }),
        "pbmbinary" => Encode(img, new PbmEncoder { ColorType = PbmColorType.BlackAndWhite, Encoding = PbmEncoding.Binary }),
        "pgmplain" => Encode(img, new PbmEncoder { ColorType = PbmColorType.Grayscale, Encoding = PbmEncoding.Plain }),
        "pgmbinary" => Encode(img, new PbmEncoder { ColorType = PbmColorType.Grayscale, Encoding = PbmEncoding.Binary }),
        "pgm16" => Encode(img, new PbmEncoder { ColorType = PbmColorType.Grayscale, Encoding = PbmEncoding.Binary, ComponentType = PbmComponentType.Short }),
        "ppmplain" => Encode(img, new PbmEncoder { ColorType = PbmColorType.Rgb, Encoding = PbmEncoding.Plain }),
        "ppmbinary" => Encode(img, new PbmEncoder { ColorType = PbmColorType.Rgb, Encoding = PbmEncoding.Binary }),
        _ => Encode(img, new PbmEncoder { ColorType = PbmColorType.Rgb, Encoding = PbmEncoding.Binary, ComponentType = PbmComponentType.Short }),
    };

    [Theory]
    [MemberData(nameof(SmallFormats))]
    public void SmallFormatsAreReadLikeImageSharpReadsThem(string kind, string name)
    {
        // Runs of one color (for the run-length packets) over noise, odd sizes.
        using var img = Noise(37, 23, Seed(kind), alpha: kind is "tga32" or "tga32rle" or "qoi4");
        for (int y = 5; y < 12; y++) for (int x = 0; x < 37; x++) img[x, y] = new Rgba32(10, 200, 90, 255);
        byte[] file = SmallFormatFile(kind, img);
        var loaded = ImageDecoder.Decode(file, name, true);
        using var reference = SixLabors.ImageSharp.Image.Load<Rgba32>(file);
        Assert.Equal((reference.Width, reference.Height), (loaded.PixelWidth, loaded.PixelHeight));
        var (max, _) = Difference(Bgra(reference), Bgra(loaded.Bitmap));
        // 16-bit pixels are widened to 8 bits with slightly different rounding by the two.
        Assert.True(max <= (kind is "tga16" or "ppm16" ? 8 : 0), $"{kind}: largest difference {max}");
        Assert.Equal(kind[..3] switch { "tga" => "TGA", "qoi" => "QOI", "pbm" => "PBM", "pgm" => "PGM", _ => "PPM" }, loaded.FormatName);
    }

    [Fact]
    public void NetpbmHeadersAndDeepGraysFollowTheStandard()
    {
        // 16-bit gray, most significant byte first (ImageSharp 3.1 writes it the other way round,
        // so this one is made by hand), with comments and odd spacing in the header.
        byte[] deep = [.. "P5 # a comment\n# another\n3\t1\n65535\n"u8, 0x00, 0x00, 0x80, 0x00, 0xFF, 0xFF];
        var gray = ImageDecoder.Decode(deep, "deep.pgm", true);
        Assert.Equal((3, 1), (gray.PixelWidth, gray.PixelHeight));
        Assert.Equal(0, Pixels.At(gray.Bitmap, 0, 0).R);
        Assert.InRange(Pixels.At(gray.Bitmap, 1, 0).G, 127, 128);
        Assert.Equal(255, Pixels.At(gray.Bitmap, 2, 0).B);
        // A largest value that is not 255: scaled up to full brightness.
        var scaled = ImageDecoder.Decode([.. "P3\n2 1\n10\n10 5 0  0 0 10\n"u8], "small.ppm", true);
        Assert.Equal(WColor.FromArgb(255, 255, 127, 0), Pixels.At(scaled.Bitmap, 0, 0));
        Assert.Equal(WColor.FromArgb(255, 0, 0, 255), Pixels.At(scaled.Bitmap, 1, 0));
        // A text bitmap needs no spaces between its digits; 1 is black.
        var bits = ImageDecoder.Decode([.. "P1\n3 2\n101\n010\n"u8], "bits.pbm", true);
        Assert.Equal(0, Pixels.At(bits.Bitmap, 0, 0).R);
        Assert.Equal(255, Pixels.At(bits.Bitmap, 1, 0).R);
        Assert.Equal(0, Pixels.At(bits.Bitmap, 1, 1).R);
    }

    [Theory]
    [MemberData(nameof(SmallFormats))]
    public void DamagedSmallFilesOnlyEverGiveTheUsualError(string kind, string name)
    {
        using var img = Noise(19, 11, 5, alpha: true);
        byte[] file = SmallFormatFile(kind, img);
        var rnd = new Random(Seed(kind));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 400; round++)
        {
            byte[] damaged = (byte[])file.Clone();
            // Some bytes changed (the header more often than the rest), and sometimes the end cut off.
            for (int n = rnd.Next(1, 6); n > 0; n--)
                damaged[rnd.Next(2) == 0 ? rnd.Next(Math.Min(24, damaged.Length)) : rnd.Next(damaged.Length)] = (byte)rnd.Next(256);
            if (rnd.Next(3) == 0) damaged = damaged[..rnd.Next(damaged.Length)];
            try
            {
                var loaded = ImageDecoder.Decode(damaged, name, true);
                Assert.True((long)loaded.PixelWidth * loaded.PixelHeight <= ImageDecoder.MaxPixels);
            }
            catch (ImageDecodeException) { }
        }
        // 400 small pictures: a header that claims a huge one must never have been believed.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 512L * 1024 * 1024);
    }

    [Fact]
    public void TinyFilesClaimingHugePicturesAreRefusedBeforeAnyMemoryIsTaken()
    {
        byte[] qoi = [.. "qoif"u8, 0, 0, 0x4E, 0x20, 0, 0, 0x4E, 0x20, 4, 0, 0xFE, 1, 2, 3, 0, 0, 0, 0, 0, 0, 0, 1]; // 20000 × 20000
        byte[] ppm = [.. "P6\n19000 19000\n255\n"u8, 1, 2, 3];
        byte[] pbm = [.. "P1\n19000 19000\n"u8, .. "0101"u8];
        var tga = new byte[18 + 30];
        tga[2] = 10; tga[12] = 0x20; tga[13] = 0x4E; tga[14] = 0x20; tga[15] = 0x4E; tga[16] = 24; // RLE, 20000 × 20000
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var (file, name) in new[] { (qoi, "a.qoi"), (ppm, "a.ppm"), (pbm, "a.pbm"), (tga, "a.tga") })
            Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(file, name, true));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32L * 1024 * 1024);
    }

    // ---- Animations ----

    private static Image<Rgba32> Film(int frames, int seed)
    {
        var img = Noise(24, 16, seed, colors: 12);
        for (int i = 1; i < frames; i++)
        {
            using var next = Noise(24, 16, seed + i, colors: 12);
            img.Frames.AddFrame(next.Frames.RootFrame);
        }
        return img;
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("webp")]
    [InlineData("png")]
    public void AnimationsHaveTheFramesAndTimesImageSharpSees(string kind)
    {
        using var film = Film(5, 11);
        for (int i = 0; i < 5; i++)
        {
            film.Frames[i].Metadata.GetGifMetadata().FrameDelay = 7 * (i + 1);
            film.Frames[i].Metadata.GetWebpMetadata().FrameDelay = (uint)(70 * (i + 1));
            film.Frames[i].Metadata.GetPngMetadata().FrameDelay = new Rational((uint)(7 * (i + 1)), 100);
        }
        byte[] file = kind switch
        {
            "gif" => Encode(film, new GifEncoder()),
            "webp" => Encode(film, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }),
            _ => Encode(film, new PngEncoder()),
        };
        var loaded = ImageDecoder.Decode(file, "film." + kind, true);
        Assert.NotNull(loaded.Animation);
        using var reference = SixLabors.ImageSharp.Image.Load<Rgba32>(file);
        Assert.Equal(reference.Frames.Count, loaded.Animation.Frames.Count);
        for (int i = 0; i < reference.Frames.Count; i++)
        {
            using var frame = reference.Frames.CloneFrame(i);
            var (max, _) = Difference(Bgra(frame), Bgra(loaded.Animation.Frames[i]));
            Assert.True(max == 0, $"{kind} frame {i}: largest difference {max}");
            Assert.Equal(70 * (i + 1), loaded.Animation.Delays[i].TotalMilliseconds);
        }
        Assert.Same(loaded.Bitmap, loaded.Animation.Frames[0]);
    }

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
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        return [.. BigEndian((uint)data.Length), .. typed, .. BigEndian(Crc(typed))];
    }

    /// <summary>The chunks of a PNG made by ImageSharp: (type, data).</summary>
    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var list = new List<(string, byte[])>();
        for (int pos = 8; pos + 12 <= png.Length;)
        {
            int length = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            list.Add((System.Text.Encoding.ASCII.GetString(png, pos + 4, 4), png[(pos + 8)..(pos + 8 + length)]));
            pos += 12 + length;
        }
        return list;
    }

    private static byte[] Solid(int width, int height, Rgba32 color)
    {
        using var img = new Image<Rgba32>(width, height, color);
        return Encode(img, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 });
    }

    [Fact]
    public void AnimatedPngFramesAreBlendedAndClearedAsTheFileSays()
    {
        // 8 × 8. Frame 0: all red. Frame 1: a half-transparent blue 4 × 4 square over it at (2,2),
        // to be cleared afterwards. Frame 2: a green 2 × 2 square replacing what is at (0,0), to
        // be put back afterwards. Frame 3: an opaque white pixel at (7,7).
        var parts = new (byte[] Png, int X, int Y, int W, int H, byte Dispose, byte Blend)[]
        {
            (Solid(8, 8, new Rgba32(255, 0, 0, 255)), 0, 0, 8, 8, 0, 0),
            (Solid(4, 4, new Rgba32(0, 0, 255, 128)), 2, 2, 4, 4, 1, 1),
            (Solid(2, 2, new Rgba32(0, 255, 0, 255)), 0, 0, 2, 2, 2, 0),
            (Solid(1, 1, new Rgba32(255, 255, 255, 255)), 7, 7, 1, 1, 0, 1),
        };
        static byte[] Be32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        var file = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        file.AddRange(Chunk("IHDR", Chunks(parts[0].Png).First(c => c.Type == "IHDR").Data));
        file.AddRange(Chunk("acTL", [.. Be32(parts.Length), .. Be32(0)]));
        int sequence = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            file.AddRange(Chunk("fcTL", [.. Be32(sequence++), .. Be32(p.W), .. Be32(p.H), .. Be32(p.X), .. Be32(p.Y), 0, 3, 0, 10, p.Dispose, p.Blend]));
            foreach (var (_, data) in Chunks(p.Png).Where(c => c.Type == "IDAT"))
                file.AddRange(i == 0 ? Chunk("IDAT", data) : Chunk("fdAT", [.. Be32(sequence++), .. data]));
        }
        file.AddRange(Chunk("IEND", []));

        var loaded = ImageDecoder.Decode([.. file], "made.png", true);
        Assert.NotNull(loaded.Animation);
        Assert.Equal(4, loaded.Animation.Frames.Count);
        Assert.All(loaded.Animation.Delays, d => Assert.Equal(300, d.TotalMilliseconds));
        WColor At(int frame, int x, int y) => Pixels.At(loaded.Animation.Frames[frame], x, y);
        var red = WColor.FromArgb(255, 255, 0, 0);
        // 1: blue at half strength over red.
        Assert.Equal(red, At(1, 0, 0));
        var mixed = At(1, 3, 3);
        Assert.Equal(255, mixed.A);
        Assert.InRange(mixed.R, 126, 128);
        Assert.InRange(mixed.B, 127, 129);
        // 2: the blue square was cleared (transparent), the green one replaces the corner.
        Assert.Equal(0, At(2, 3, 3).A);
        Assert.Equal(WColor.FromArgb(255, 0, 255, 0), At(2, 1, 1));
        Assert.Equal(red, At(2, 6, 1));
        // 3: the corner is red again, the white pixel is there.
        Assert.Equal(red, At(3, 1, 1));
        Assert.Equal(WColor.FromArgb(255, 255, 255, 255), At(3, 7, 7));
        Assert.Equal(0, At(3, 3, 3).A);
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("webp")]
    [InlineData("png")]
    public void DamagedAnimationsOnlyEverGiveTheUsualError(string kind)
    {
        using var film = Film(4, 3);
        byte[] file = kind switch
        {
            "gif" => Encode(film, new GifEncoder()),
            "webp" => Encode(film, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }),
            _ => Encode(film, new PngEncoder()),
        };
        var rnd = new Random(kind.Length * 17);
        for (int round = 0; round < 250; round++)
        {
            byte[] damaged = (byte[])file.Clone();
            for (int n = rnd.Next(1, 5); n > 0; n--) damaged[rnd.Next(8, damaged.Length)] = (byte)rnd.Next(256);
            if (rnd.Next(3) == 0) damaged = damaged[..rnd.Next(12, damaged.Length)];
            try
            {
                var loaded = ImageDecoder.Decode(damaged, "film." + kind, true);
                Assert.True(loaded.PixelWidth > 0);
            }
            catch (ImageDecodeException) { }
        }
    }

    // ---- Saving GIF ----

    private static async Task<byte[]> SaveGif(BitmapSource bitmap)
    {
        using var tmp = new TempFolder();
        string path = tmp.File("out.gif");
        await StaTask.Run(() => { ImageSaver.Save(bitmap, path, 90); return true; });
        return File.ReadAllBytes(path);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 9, 7)]
    [InlineData(3, 40, 30)]
    [InlineData(4, 40, 30)]
    [InlineData(5, 64, 64)]
    [InlineData(16, 64, 64)]
    [InlineData(17, 90, 70)]
    [InlineData(128, 120, 90)]
    [InlineData(129, 120, 90)]
    [InlineData(255, 200, 150)]
    [InlineData(256, 300, 300)]
    public async Task GifWithFewColorsIsWrittenExactly(int colors, int width, int height)
    {
        // Noise is the hardest thing for the compression: its table fills up and starts again.
        using var img = Noise(width, height, colors * 31 + width, colors: colors);
        byte[] gif = await SaveGif(Bitmap(img));
        // Two readers that have nothing in common must both see the same pixels.
        using var reference = SixLabors.ImageSharp.Image.Load<Rgba32>(gif);
        Assert.Equal(0, Difference(Bgra(img), Bgra(reference)).Max);
        var wic = new GifBitmapDecoder(new MemoryStream(gif), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(0, Difference(Bgra(img), Bgra(wic)).Max);
        var again = ImageDecoder.Decode(gif, "out.gif", true);
        Assert.Equal(0, Difference(Bgra(img), Bgra(again.Bitmap)).Max);
    }

    [Fact]
    public async Task GifKeepsTransparencyAndReducesAPhotoSensibly()
    {
        // Thousands of colors, the left quarter transparent.
        using var img = Scene(160, 120);
        for (int y = 0; y < 120; y++) for (int x = 0; x < 40; x++) img[x, y] = new Rgba32(9, 9, 9, 0);
        byte[] gif = await SaveGif(Bitmap(img));
        using var reference = SixLabors.ImageSharp.Image.Load<Rgba32>(gif);
        Assert.Equal((160, 120), (reference.Width, reference.Height));
        long error = 0;
        for (int y = 0; y < 120; y++)
        {
            for (int x = 0; x < 160; x++)
            {
                var (was, now) = (img[x, y], reference[x, y]);
                Assert.Equal(x < 40 ? 0 : 255, now.A);
                if (x >= 40) error += Math.Abs(was.R - now.R) + Math.Abs(was.G - now.G) + Math.Abs(was.B - now.B);
            }
        }
        double mean = error / (120.0 * 120 * 3);
        // What ImageSharp makes of the same picture, as the yardstick.
        using var theirs = SixLabors.ImageSharp.Image.Load<Rgba32>(Encode(img, new GifEncoder()));
        long theirError = 0;
        for (int y = 0; y < 120; y++)
            for (int x = 40; x < 160; x++)
                theirError += Math.Abs(img[x, y].R - theirs[x, y].R) + Math.Abs(img[x, y].G - theirs[x, y].G) + Math.Abs(img[x, y].B - theirs[x, y].B);
        double theirMean = theirError / (120.0 * 120 * 3);
        Assert.True(mean < theirMean * 1.2, $"mean color error {mean:0.0} (ImageSharp: {theirMean:0.0})");
        // And a picture that is nothing but transparent.
        using var empty = new Image<Rgba32>(5, 4, new Rgba32(0, 0, 0, 0));
        using var readBack = SixLabors.ImageSharp.Image.Load<Rgba32>(await SaveGif(Bitmap(empty)));
        Assert.Equal(0, readBack[2, 2].A);
    }

    // ---- Filters and resizing, against ImageSharp's ----

    public static TheoryData<string> ColorFilters() => ["invert", "gray", "sepia", "bright+", "bright-", "contrast+", "contrast-",
                                                         "both", "hue", "hue-", "saturate", "desaturate", "huesat"];

    [Theory]
    [MemberData(nameof(ColorFilters))]
    public void ColorFiltersMatchImageSharp(string filter)
    {
        using var img = Noise(48, 32, 77, alpha: true);
        var source = Bitmap(img);
        (BitmapSource mine, Action<IImageProcessingContext> theirs) = filter switch
        {
            "invert" => (ImageOps.Invert(source), x => x.Invert()),
            "gray" => (ImageOps.Grayscale(source), x => x.Grayscale()),
            "sepia" => (ImageOps.Sepia(source), x => x.Sepia()),
            "bright+" => (ImageOps.BrightnessContrast(source, 30, 0), x => x.Brightness(1.3f)),
            "bright-" => (ImageOps.BrightnessContrast(source, -45, 0), x => x.Brightness(0.55f)),
            "contrast+" => (ImageOps.BrightnessContrast(source, 0, 40), x => x.Contrast(1.4f)),
            "contrast-" => (ImageOps.BrightnessContrast(source, 0, -60), x => x.Contrast(0.4f)),
            "both" => (ImageOps.BrightnessContrast(source, 20, -30), x => x.Brightness(1.2f).Contrast(0.7f)),
            "hue" => (ImageOps.HueSaturation(source, 75, 0), x => x.Hue(75)),
            "hue-" => (ImageOps.HueSaturation(source, -140, 0), x => x.Hue(-140)),
            "saturate" => (ImageOps.HueSaturation(source, 0, 60), x => x.Saturate(1.6f)),
            "desaturate" => (ImageOps.HueSaturation(source, 0, -80), x => x.Saturate(0.2f)),
            _ => (ImageOps.HueSaturation(source, 30, 25), (Action<IImageProcessingContext>)(x => x.Hue(30).Saturate(1.25f))),
        };
        using var expected = img.Clone();
        // One step at a time, like pViewer always did (each step rounds to 8 bits).
        if (filter == "both") { expected.Mutate(x => x.Brightness(1.2f)); expected.Mutate(x => x.Contrast(0.7f)); }
        else if (filter == "huesat") { expected.Mutate(x => x.Hue(30)); expected.Mutate(x => x.Saturate(1.25f)); }
        else expected.Mutate(theirs);
        var (max, mean) = Difference(Bgra(expected), Bgra(mine));
        Assert.True(max <= 2 && mean < 0.3, $"{filter}: largest difference {max}, mean {mean:0.00}");
    }

    [Theory]
    [InlineData(200, 150, 50, 37, false)]   // much smaller
    [InlineData(200, 150, 140, 100, false)] // a little smaller
    [InlineData(60, 40, 150, 100, false)]   // larger
    [InlineData(200, 150, 61, 150, false)]  // one side only
    [InlineData(120, 90, 40, 30, true)]     // with transparency
    public void ResizingIsAsGoodAsImageSharps(int w, int h, int newW, int newH, bool alpha)
    {
        using var img = Scene(w, h, alpha);
        var mine = ImageOps.Resize(Bitmap(img), newW, newH);
        Assert.Equal((newW, newH), (mine.PixelWidth, mine.PixelHeight));
        bool down = (long)newW * newH < (long)w * h;
        using var expected = img.Clone(x => x.Resize(newW, newH, down ? KnownResamplers.Lanczos3 : KnownResamplers.Bicubic));
        var (max, mean) = Difference(Bgra(expected), Bgra(mine));
        Assert.True(mean < 1.0 && max <= 24, $"largest difference {max}, mean {mean:0.00}");
    }

    [Fact]
    public void ResizingDoesNotLetTheColorOfTransparentPixelsShow()
    {
        // Opaque white next to fully transparent pure green: no green may leak into the edge.
        using var img = new Image<Rgba32>(64, 64);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) img[x, y] = x < 32 ? new Rgba32(255, 255, 255, 255) : new Rgba32(0, 255, 0, 0);
        var small = ImageOps.Resize(Bitmap(img), 16, 16);
        for (int x = 0; x < 16; x++)
        {
            var c = Pixels.At(small, x, 8);
            if (c.A > 0) Assert.True(c.R >= 250 && c.B >= 250, $"x={x}: {c}");
        }
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(4)]
    [InlineData(12)]
    public void BlurAndSharpenMatchImageSharp(double sigma)
    {
        using var img = Scene(160, 120);
        var source = Bitmap(img);
        using var blurred = img.Clone(x => x.GaussianBlur((float)sigma));
        var blur = Difference(Bgra(blurred), Bgra(ImageOps.Blur(source, sigma)));
        Assert.True(blur.Mean < 1.5 && blur.Max <= 20, $"blur {sigma}: largest difference {blur.Max}, mean {blur.Mean:0.00}");
        using var sharpened = img.Clone(x => x.GaussianSharpen((float)sigma));
        var sharp = Difference(Bgra(sharpened), Bgra(ImageOps.Sharpen(source, sigma)));
        Assert.True(sharp.Mean < 3, $"sharpen {sigma}: largest difference {sharp.Max}, mean {sharp.Mean:0.00}");
    }

    [Fact]
    public void BlurAndSharpenLeaveAFlatPictureAlone()
    {
        // Also at the edges: nothing dark or transparent may creep in from outside the picture.
        using var img = new Image<Rgba32>(50, 40, new Rgba32(200, 120, 40, 255));
        var source = Bitmap(img);
        foreach (var result in new[] { ImageOps.Blur(source, 6), ImageOps.Sharpen(source, 6) })
        {
            foreach (var (x, y) in new[] { (0, 0), (49, 0), (0, 39), (49, 39), (25, 20), (0, 20), (25, 0) })
                Assert.Equal(WColor.FromArgb(255, 200, 120, 40), Pixels.At(result, x, y));
        }
        // A half-transparent picture keeps its transparency.
        using var veil = new Image<Rgba32>(30, 30, new Rgba32(10, 20, 30, 128));
        Assert.Equal(WColor.FromArgb(128, 10, 20, 30), Pixels.At(ImageOps.Blur(Bitmap(veil), 3), 15, 15));
    }

    [Fact]
    public void BorderAndFlatteningWorkOnTransparentPictures()
    {
        using var img = new Image<Rgba32>(4, 3, new Rgba32(0, 0, 255, 128));
        var framed = ImageOps.AddBorder(Bitmap(img), 2, WColor.FromArgb(255, 9, 8, 7));
        Assert.Equal((8, 7), (framed.PixelWidth, framed.PixelHeight));
        Assert.Equal(WColor.FromArgb(255, 9, 8, 7), Pixels.At(framed, 1, 1));
        Assert.Equal(WColor.FromArgb(128, 0, 0, 255), Pixels.At(framed, 2, 2));
        Assert.Equal(WColor.FromArgb(255, 9, 8, 7), Pixels.At(framed, 7, 6));
        var flat = ImageOps.FlattenAlpha(Bitmap(img), WColor.FromRgb(255, 255, 255));
        var c = Pixels.At(flat, 1, 1);
        Assert.Equal(255, c.A);
        Assert.InRange(c.R, 126, 128);
        Assert.Equal(255, c.B);
    }

    // ---- Saving WebP ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebpIsWrittenWithItsTransparency(bool lossless)
    {
        using var img = Scene(80, 60, alpha: true);
        using var tmp = new TempFolder();
        string path = tmp.File("out.webp");
        await StaTask.Run(() => { ImageSaver.Save(Bitmap(img), path, 85, losslessWebp: lossless); return true; });
        byte[] file = File.ReadAllBytes(path);
        Assert.Equal(lossless, ImageDecoder.IsLosslessWebp(file));
        using var reference = SixLabors.ImageSharp.Image.Load<Rgba32>(file);
        var (max, mean) = Difference(Bgra(img), Bgra(reference));
        if (lossless) Assert.Equal(0, max);
        else Assert.True(mean < 4, $"mean difference {mean:0.0}");
    }
}
