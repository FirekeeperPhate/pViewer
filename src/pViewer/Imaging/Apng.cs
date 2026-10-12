namespace pViewer.Imaging;

/// <summary>
/// Animated PNG. Each frame of the file is a PNG picture of its own without a header: here it is
/// given one and handed to the normal PNG decoder, then the frames are put together as the file
/// says (over the previous one or replacing it, left there or cleared afterwards). Nothing is
/// decompressed here: only the list of chunks is read.
/// </summary>
internal static class Apng
{
    public sealed record Animation(List<PixelBuffer> Frames, List<int> DurationsMs);

    private sealed class Frame
    {
        public int Width, Height, X, Y, DelayMs, Dispose, Blend;
        public readonly List<(int Start, int Length)> Data = [];
    }

    /// <param name="decodePng">Decodes a complete PNG file into pixels.</param>
    public static Animation Decode(byte[] d, int maxFrames, Func<byte[], PixelBuffer> decodePng)
    {
        if (d.Length < 8 + 25 || d[0] != 0x89 || d[1] != 'P') throw new InvalidDataException("Not a PNG.");
        byte[]? header = null;
        var shared = new List<(int Start, int Length)>(); // palette, transparency, color chunks: every frame needs them
        var frames = new List<Frame>();
        Frame? current = null, plainImage = null;
        bool imageDataSeen = false;
        int pos = 8;
        while (pos + 12 <= d.Length)
        {
            int length = BigEndian(d, pos);
            if (length < 0 || pos + 12L + length > d.Length) break; // cut short: the frames read so far
            string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
            int data = pos + 8;
            if (type == "IEND") break;
            switch (type)
            {
                case "IHDR" when length == 13:
                    header = d.AsSpan(data, 13).ToArray();
                    break;
                case "PLTE" or "tRNS" or "gAMA" or "cHRM" or "sRGB" or "sBIT" when !imageDataSeen:
                    shared.Add((pos, 12 + length));
                    break;
                case "fcTL" when length == 26:
                    if (frames.Count >= maxFrames) throw new InvalidDataException("Too many frames.");
                    int delay = (d[data + 20] << 8) | d[data + 21], per = (d[data + 22] << 8) | d[data + 23];
                    current = new Frame
                    {
                        Width = BigEndian(d, data + 4), Height = BigEndian(d, data + 8),
                        X = BigEndian(d, data + 12), Y = BigEndian(d, data + 16),
                        DelayMs = (int)(delay * 1000L / (per == 0 ? 100 : per)),
                        Dispose = d[data + 24], Blend = d[data + 25],
                    };
                    frames.Add(current);
                    break;
                case "IDAT":
                    // The plain image is the first frame only if a frame was announced before it.
                    if (!imageDataSeen) plainImage = current;
                    imageDataSeen = true;
                    if (plainImage is not null && ReferenceEquals(current, plainImage)) current.Data.Add((data, length));
                    break;
                case "fdAT" when length >= 4 && current is not null && imageDataSeen:
                    current.Data.Add((data + 4, length - 4)); // after the sequence number
                    break;
            }
            pos += 12 + length;
        }
        if (header is null) throw new InvalidDataException("The PNG has no header.");
        int width = BigEndian(header, 0), height = BigEndian(header, 4);
        frames.RemoveAll(f => f.Data.Count == 0);
        if (frames.Count == 0) throw new InvalidDataException("No frames.");

        var canvas = new PixelBuffer(width, height);
        var result = new Animation(new List<PixelBuffer>(frames.Count), new List<int>(frames.Count));
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (f.Width < 1 || f.Height < 1 || f.X < 0 || f.Y < 0 || (long)f.X + f.Width > width || (long)f.Y + f.Height > height)
                throw new InvalidDataException("A frame lies outside the image.");
            var picture = decodePng(FramePng(d, header, shared, f));
            if (picture.Width != f.Width || picture.Height != f.Height) throw new InvalidDataException("A frame has another size than declared.");

            // "Put back what was there" (2): on the first frame it means "clear", like 1.
            int dispose = f.Dispose == 2 && i == 0 ? 1 : f.Dispose;
            byte[]? before = dispose == 2 ? CopyRegion(canvas, f) : null;
            Draw(canvas, picture, f);
            var shown = new PixelBuffer(width, height);
            Buffer.BlockCopy(canvas.Data, 0, shown.Data, 0, shown.Data.Length);
            result.Frames.Add(shown);
            result.DurationsMs.Add(f.DelayMs);
            if (dispose == 1) ClearRegion(canvas, f);
            else if (before is not null) RestoreRegion(canvas, f, before);
        }
        return result;
    }

    /// <summary>A PNG file holding just this frame: signature, header with the frame's size, shared chunks, data, end.</summary>
    private static byte[] FramePng(byte[] d, byte[] header, List<(int Start, int Length)> shared, Frame f)
    {
        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = (byte[])header.Clone();
        WriteBigEndian(ihdr, 0, f.Width);
        WriteBigEndian(ihdr, 4, f.Height);
        WriteChunk(png, "IHDR", ihdr, 0, ihdr.Length);
        foreach (var (start, length) in shared) png.Write(d, start, length);
        foreach (var (start, length) in f.Data) WriteChunk(png, "IDAT", d, start, length);
        WriteChunk(png, "IEND", [], 0, 0);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data, int start, int length)
    {
        Span<byte> four = stackalloc byte[4];
        four[0] = (byte)(length >> 24); four[1] = (byte)(length >> 16); four[2] = (byte)(length >> 8); four[3] = (byte)length;
        png.Write(four);
        Span<byte> name = stackalloc byte[4];
        for (int i = 0; i < 4; i++) name[i] = (byte)type[i];
        png.Write(name);
        png.Write(data, start, length);
        uint crc = Crc(Crc(0xFFFFFFFF, name), data.AsSpan(start, length)) ^ 0xFFFFFFFF;
        four[0] = (byte)(crc >> 24); four[1] = (byte)(crc >> 16); four[2] = (byte)(crc >> 8); four[3] = (byte)crc;
        png.Write(four);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void Draw(PixelBuffer canvas, PixelBuffer picture, Frame f)
    {
        byte[] c = canvas.Data, s = picture.Data;
        for (int y = 0; y < f.Height; y++)
        {
            int to = ((f.Y + y) * canvas.Width + f.X) * 4, from = y * f.Width * 4;
            if (f.Blend != 1)
            {
                Buffer.BlockCopy(s, from, c, to, f.Width * 4); // replaces what was there, transparency included
                continue;
            }
            for (int x = 0; x < f.Width; x++, to += 4, from += 4)
            {
                int sa = s[from + 3];
                if (sa == 0) continue;
                int da = c[to + 3];
                if (sa == 255 || da == 0)
                {
                    c[to] = s[from]; c[to + 1] = s[from + 1]; c[to + 2] = s[from + 2]; c[to + 3] = (byte)sa;
                    continue;
                }
                // "Over": the frame's color by its alpha, what is under it by what is left.
                int under = da * (255 - sa); // × 255
                int alpha = sa * 255 + under; // × 255
                for (int k = 0; k < 3; k++)
                    c[to + k] = (byte)((s[from + k] * sa * 255 + c[to + k] * under + alpha / 2) / alpha);
                c[to + 3] = (byte)((alpha + 127) / 255);
            }
        }
    }

    private static byte[] CopyRegion(PixelBuffer canvas, Frame f)
    {
        var copy = new byte[f.Width * f.Height * 4];
        for (int y = 0; y < f.Height; y++)
            Buffer.BlockCopy(canvas.Data, ((f.Y + y) * canvas.Width + f.X) * 4, copy, y * f.Width * 4, f.Width * 4);
        return copy;
    }

    private static void RestoreRegion(PixelBuffer canvas, Frame f, byte[] copy)
    {
        for (int y = 0; y < f.Height; y++)
            Buffer.BlockCopy(copy, y * f.Width * 4, canvas.Data, ((f.Y + y) * canvas.Width + f.X) * 4, f.Width * 4);
    }

    private static void ClearRegion(PixelBuffer canvas, Frame f)
    {
        for (int y = 0; y < f.Height; y++)
            Array.Clear(canvas.Data, ((f.Y + y) * canvas.Width + f.X) * 4, f.Width * 4);
    }

    private static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    private static void WriteBigEndian(byte[] b, int at, int value)
    {
        b[at] = (byte)(value >> 24); b[at + 1] = (byte)(value >> 16); b[at + 2] = (byte)(value >> 8); b[at + 3] = (byte)value;
    }
}
