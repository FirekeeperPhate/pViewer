using System.IO.Compression;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace pViewer.Tests;

/// <summary>Genera immagini e archivi di prova in una cartella temporanea.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pviewer_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

internal static class TestImages
{
    /// <summary>Immagine divisa a metà: sinistra rossa, destra blu.</summary>
    public static Image<Rgba32> SplitImage(int width, int height)
    {
        var img = new Image<Rgba32>(width, height, new Rgba32(255, 0, 0));
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = width / 2; x < width; x++) row[x] = new Rgba32(0, 0, 255);
            }
        });
        return img;
    }

    public static byte[] Png(int width, int height, Rgba32? color = null)
    {
        using var img = new Image<Rgba32>(width, height, color ?? new Rgba32(10, 200, 30));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>JPEG con tag EXIF Orientation e marca della fotocamera.</summary>
    public static byte[] JpegWithOrientation(int width, int height, ushort orientation, string make = "TestCam")
    {
        using var img = SplitImage(width, height);
        img.Metadata.ExifProfile = new ExifProfile();
        img.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
        img.Metadata.ExifProfile.SetValue(ExifTag.Make, make);
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 95 });
        return ms.ToArray();
    }

    public static void Zip(string path, params (string Name, byte[]? Data)[] entries)
    {
        using var fs = System.IO.File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var entry = zip.CreateEntry(name);
            if (data is null) continue; // cartella
            using var s = entry.Open();
            s.Write(data);
        }
    }

    public static byte[] ZipBytes(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }
}
