using MetadataExtractor;
using pViewer.ViewModels;

namespace pViewer.Services;

public static class MetadataService
{
    /// <summary>More linked EXIF/TIFF blocks than any real file has (cameras write a handful).</summary>
    private const int MaxIfdChain = 1000;

    /// <summary>
    /// <see cref="Read"/> on a thread with a large stack: the library follows linked EXIF blocks by
    /// recursion, and a crafted chain would otherwise overflow the stack, which ends the process.
    /// </summary>
    public static Task<IReadOnlyList<MetadataGroup>> ReadAsync(byte[] data)
    {
        var done = new TaskCompletionSource<IReadOnlyList<MetadataGroup>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(Read(data)); }
            catch (Exception ex) { done.SetException(ex); }
        }, maxStackSize: 256 * 1024 * 1024) { IsBackground = true, Name = "Metadata" };
        thread.Start();
        return done.Task;
    }

    /// <summary>Reads EXIF, IPTC, XMP and format data, grouped by section.</summary>
    public static IReadOnlyList<MetadataGroup> Read(byte[] data)
    {
        if (HasEndlessIfdChain(data)) return []; // crafted file: no metadata rather than a crash
        data = Imaging.ImageDecoder.WithoutPngBombs(data); // chunks inflating to GBs are left out
        using var ms = new MemoryStream(data, writable: false);
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(ms);
        }
        catch (ImageProcessingException)
        {
            return []; // format MetadataExtractor does not know (QOI, TGA…): simply no metadata
        }
        var groups = new List<MetadataGroup>();
        foreach (var dir in directories)
        {
            var tags = dir.Tags
                .Where(t => !string.IsNullOrWhiteSpace(t.Description))
                .Select(t => new KeyValuePair<string, string>(t.Name, t.Description!.Trim()))
                .ToList();
            if (tags.Count > 0) groups.Add(new MetadataGroup(dir.Name, tags));
        }
        return groups;
    }

    /// <summary>
    /// True if a TIFF file, or the EXIF block of a JPEG, chains more IFDs than <see cref="MaxIfdChain"/>.
    /// </summary>
    internal static bool HasEndlessIfdChain(byte[] d)
    {
        if (d.Length >= 8 && IsTiffHeader(d, 0)) return ChainTooLong(d, 0, d.Length);
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return false;
        int pos = 2;
        while (pos + 4 <= d.Length && d[pos] == 0xFF)
        {
            byte marker = d[pos + 1];
            if (marker is 0xD9 or 0xDA) break; // end of image, start of the image data
            int length = (d[pos + 2] << 8) | d[pos + 3];
            if (length < 2 || pos + 2 + length > d.Length) break;
            // APP1 "Exif\0\0" followed by a TIFF structure.
            if (marker == 0xE1 && length >= 16 && d[pos + 4] == 'E' && d[pos + 5] == 'x' && d[pos + 6] == 'i'
                && d[pos + 7] == 'f' && IsTiffHeader(d, pos + 10) && ChainTooLong(d, pos + 10, length - 8))
                return true;
            pos += 2 + length;
        }
        return false;
    }

    private static bool IsTiffHeader(byte[] d, int at) =>
        at + 8 <= d.Length && ((d[at] == 'I' && d[at + 1] == 'I' && d[at + 2] == 42 && d[at + 3] == 0)
                               || (d[at] == 'M' && d[at + 1] == 'M' && d[at + 2] == 0 && d[at + 3] == 42));

    private static bool ChainTooLong(byte[] d, int tiff, int length)
    {
        bool little = d[tiff] == 'I';
        int end = Math.Min(d.Length, tiff + length);
        uint U16(int at) => little ? (uint)(d[at] | d[at + 1] << 8) : (uint)(d[at] << 8 | d[at + 1]);
        uint U32(int at) => little ? (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24)
                                   : (uint)(d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3]);
        var seen = new HashSet<uint>();
        uint offset = U32(tiff + 4);
        int count = 0;
        while (offset >= 8 && tiff + (long)offset + 2 <= end && seen.Add(offset))
        {
            if (++count > MaxIfdChain) return true;
            long next = tiff + (long)offset + 2 + U16(tiff + (int)offset) * 12L;
            if (next + 4 > end) break;
            offset = U32((int)next);
        }
        return false;
    }
}
