using pViewer.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace pViewer.Tests;

public class AnimationTests
{
    private static readonly Rgba32[] Colors = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255)];

    /// <summary>Tre fotogrammi rosso/verde/blu; ritardi in unità del formato (GIF: centesimi, WebP: ms).</summary>
    private static Image<Rgba32> ThreeFrames(Action<ImageFrame<Rgba32>, int> setDelay)
    {
        var img = new Image<Rgba32>(8, 6, Colors[0]);
        setDelay(img.Frames.RootFrame, 0);
        for (int i = 1; i < 3; i++)
        {
            using var f = new Image<Rgba32>(8, 6, Colors[i]);
            var added = img.Frames.AddFrame(f.Frames.RootFrame);
            setDelay(added, i);
        }
        return img;
    }

    [Fact]
    public void DecodesAnimatedGifFramesAndDelays()
    {
        using var img = ThreeFrames((f, i) => f.Metadata.GetGifMetadata().FrameDelay = i == 0 ? 0 : 25);
        using var ms = new MemoryStream();
        img.SaveAsGif(ms);

        var loaded = ImageDecoder.Decode(ms.ToArray(), "anim.gif", true);
        Assert.NotNull(loaded.Animation);
        Assert.Equal(3, loaded.Animation!.Frames.Count);
        Assert.Same(loaded.Bitmap, loaded.Animation.Frames[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(100), loaded.Animation.Delays[0]); // 0 → 100 ms come i browser
        Assert.Equal(TimeSpan.FromMilliseconds(250), loaded.Animation.Delays[1]);
        Assert.True(Pixels.IsBluish(Pixels.At(loaded.Animation.Frames[2], 3, 3)));
        Assert.Equal("GIF", loaded.FormatName);
    }

    [Fact]
    public void DecodesAnimatedWebp()
    {
        using var img = ThreeFrames((f, _) => f.Metadata.GetWebpMetadata().FrameDelay = 40);
        using var ms = new MemoryStream();
        img.SaveAsWebp(ms, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });

        var loaded = ImageDecoder.Decode(ms.ToArray(), "anim.webp", true);
        Assert.Equal(3, loaded.Animation!.Frames.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(40), loaded.Animation.Delays[1]);
        Assert.True(Pixels.IsReddish(Pixels.At(loaded.Animation.Frames[0], 1, 1)));
    }

    [Fact]
    public void StaticGifHasNoAnimation()
    {
        using var img = new Image<Rgba32>(5, 5, Colors[1]);
        using var ms = new MemoryStream();
        img.SaveAsGif(ms);
        var loaded = ImageDecoder.Decode(ms.ToArray(), "static.gif", true);
        Assert.Null(loaded.Animation);
        Assert.Equal(5, loaded.PixelWidth);
    }
}
