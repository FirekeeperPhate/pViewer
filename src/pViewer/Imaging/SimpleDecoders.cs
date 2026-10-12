namespace pViewer.Imaging;

/// <summary>
/// Three small formats that neither Windows nor Skia read: QOI, the Netpbm family (PBM, PGM, PPM)
/// and TGA. Their files may come from anywhere: the size they declare is checked against the bytes
/// really there before any memory is taken, and every read stays inside the file.
/// </summary>
internal static class SimpleDecoders
{
    /// <returns>null if the file is none of the three; otherwise the image (or an exception for a damaged file).</returns>
    public static PixelBuffer? TryDecode(byte[] d, string name, long maxPixels, out string format)
    {
        format = "";
        if (d.Length >= 14 && d[0] == 'q' && d[1] == 'o' && d[2] == 'i' && d[3] == 'f')
        {
            format = "QOI";
            return DecodeQoi(d, maxPixels);
        }
        if (d.Length >= 3 && d[0] == 'P' && d[1] is >= (byte)'1' and <= (byte)'6' && IsSpace(d[2]))
        {
            format = d[1] is (byte)'1' or (byte)'4' ? "PBM" : d[1] is (byte)'2' or (byte)'5' ? "PGM" : "PPM";
            return DecodePnm(d, maxPixels);
        }
        // TGA has no signature at its start: the extension, or the footer newer files end with.
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".tga" or ".icb" or ".vda" or ".vst" || HasTgaFooter(d))
        {
            format = "TGA";
            return DecodeTga(d, maxPixels);
        }
        return null;
    }

    private static void CheckSize(long width, long height, long maxPixels)
    {
        if (width < 1 || height < 1) throw new InvalidDataException("The image has no size.");
        if (width * height > maxPixels) throw new InvalidDataException($"The image is too large ({width} × {height}).");
    }

    // ---- QOI (qoiformat.org) ----

    private static PixelBuffer DecodeQoi(byte[] d, long maxPixels)
    {
        long width = (uint)((d[4] << 24) | (d[5] << 16) | (d[6] << 8) | d[7]);
        long height = (uint)((d[8] << 24) | (d[9] << 16) | (d[10] << 8) | d[11]);
        CheckSize(width, height, maxPixels);
        // One byte describes at most 62 pixels: a short file cannot hold a big picture.
        if ((d.Length - 14L) * 62 < width * height) throw new InvalidDataException("The QOI data is shorter than the image.");
        var image = new PixelBuffer((int)width, (int)height);
        byte[] o = image.Data;
        Span<byte> seen = stackalloc byte[64 * 4]; // r, g, b, a of the 64 colors seen last, by hash
        seen.Clear();
        byte r = 0, g = 0, b = 0, a = 255;
        int p = 14, run = 0, end = (int)(width * height) * 4;
        for (int at = 0; at < end; at += 4)
        {
            if (run > 0) run--;
            else if (p < d.Length)
            {
                int tag = d[p++];
                if (tag == 0xFE && p + 3 <= d.Length) { r = d[p]; g = d[p + 1]; b = d[p + 2]; p += 3; }
                else if (tag == 0xFF && p + 4 <= d.Length) { r = d[p]; g = d[p + 1]; b = d[p + 2]; a = d[p + 3]; p += 4; }
                else if ((tag & 0xC0) == 0x00) { int i = tag * 4; r = seen[i]; g = seen[i + 1]; b = seen[i + 2]; a = seen[i + 3]; }
                else if ((tag & 0xC0) == 0x40)
                {
                    r += (byte)(((tag >> 4) & 3) - 2);
                    g += (byte)(((tag >> 2) & 3) - 2);
                    b += (byte)((tag & 3) - 2);
                }
                else if ((tag & 0xC0) == 0x80 && p < d.Length)
                {
                    int second = d[p++];
                    int dg = (tag & 0x3F) - 32;
                    r += (byte)(dg - 8 + ((second >> 4) & 0x0F));
                    g += (byte)dg;
                    b += (byte)(dg - 8 + (second & 0x0F));
                }
                else if ((tag & 0xC0) == 0xC0) run = tag & 0x3F; // this pixel, then that many more
                int hash = (r * 3 + g * 5 + b * 7 + a * 11) % 64 * 4;
                seen[hash] = r; seen[hash + 1] = g; seen[hash + 2] = b; seen[hash + 3] = a;
            }
            // Past the end of a file cut short: the last color goes on.
            o[at] = b; o[at + 1] = g; o[at + 2] = r; o[at + 3] = a;
        }
        return image;
    }

    // ---- Netpbm: P1-P3 as text, P4-P6 as bytes ----

    private static bool IsSpace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0B or 0x0C;

    private static PixelBuffer DecodePnm(byte[] d, long maxPixels)
    {
        int kind = d[1] - '0';
        int p = 2;
        long width = HeaderNumber(d, ref p), height = HeaderNumber(d, ref p);
        long max = kind is 1 or 4 ? 1 : HeaderNumber(d, ref p);
        CheckSize(width, height, maxPixels);
        if (max is < 1 or > 65535) throw new InvalidDataException("The largest value is not valid.");
        int w = (int)width, h = (int)height, channels = kind is 3 or 6 ? 3 : 1;
        long samples = width * height * channels;
        bool binary = kind >= 4;
        if (binary)
        {
            p++; // the single white space after the header
            long needed = kind == 4 ? (width + 7) / 8 * height : samples * (max > 255 ? 2 : 1);
            if (p > d.Length || d.Length - p < needed) throw new InvalidDataException("The image data is cut short.");
        }
        else if (d.Length - p < samples)
        {
            throw new InvalidDataException("The image data is cut short."); // at least one character per value
        }

        var image = new PixelBuffer(w, h);
        byte[] o = image.Data;
        int at = 0;
        if (kind == 4)
        {
            int rowBytes = (w + 7) / 8;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++, at += 4)
                {
                    bool black = (d[p + y * rowBytes + x / 8] & (0x80 >> (x % 8))) != 0;
                    o[at] = o[at + 1] = o[at + 2] = black ? (byte)0 : (byte)255;
                    o[at + 3] = 255;
                }
            }
            return image;
        }
        Span<byte> value = stackalloc byte[3];
        for (long i = 0; i < width * height; i++, at += 4)
        {
            for (int c = 0; c < channels; c++)
            {
                long v;
                if (binary) v = max > 255 ? (d[p++] << 8) | d[p++] : d[p++];
                else if (kind == 1) v = TextBit(d, ref p);
                else v = TextNumber(d, ref p);
                value[c] = kind == 1 ? (v != 0 ? (byte)0 : (byte)255) // 1 is black
                    : (byte)(Math.Min(v, max) * 255 / max);
            }
            if (channels == 1) value[1] = value[2] = value[0];
            o[at] = value[2]; o[at + 1] = value[1]; o[at + 2] = value[0]; o[at + 3] = 255;
        }
        return image;
    }

    /// <summary>A number of the header: white space and "# comments" may come before it.</summary>
    private static long HeaderNumber(byte[] d, ref int p)
    {
        while (p < d.Length)
        {
            if (d[p] == '#') { while (p < d.Length && d[p] != '\n') p++; }
            else if (IsSpace(d[p])) p++;
            else break;
        }
        return Digits(d, ref p);
    }

    private static long TextNumber(byte[] d, ref int p)
    {
        while (p < d.Length && !(d[p] >= '0' && d[p] <= '9')) p++;
        return Digits(d, ref p);
    }

    /// <summary>In a text bitmap the 0s and 1s need no space between them.</summary>
    private static long TextBit(byte[] d, ref int p)
    {
        while (p < d.Length && d[p] != '0' && d[p] != '1') p++;
        if (p >= d.Length) throw new InvalidDataException("The image data is cut short.");
        return d[p++] - '0';
    }

    private static long Digits(byte[] d, ref int p)
    {
        if (p >= d.Length || d[p] < '0' || d[p] > '9') throw new InvalidDataException("A number is missing.");
        long value = 0;
        while (p < d.Length && d[p] >= '0' && d[p] <= '9')
        {
            value = value * 10 + (d[p++] - '0');
            if (value > int.MaxValue) throw new InvalidDataException("A number is too large.");
        }
        return value;
    }

    // ---- TGA ----

    private static bool HasTgaFooter(byte[] d) =>
        d.Length >= 44 && System.Text.Encoding.ASCII.GetString(d, d.Length - 18, 16) == "TRUEVISION-XFILE";

    private static PixelBuffer DecodeTga(byte[] d, long maxPixels)
    {
        if (d.Length < 18) throw new InvalidDataException("The TGA header is cut short.");
        int idLength = d[0], mapType = d[1], type = d[2];
        int mapFirst = d[3] | (d[4] << 8), mapLength = d[5] | (d[6] << 8), mapBits = d[7];
        int width = d[12] | (d[13] << 8), height = d[14] | (d[15] << 8), bits = d[16], descriptor = d[17];
        bool rle = type is 9 or 10 or 11;
        int baseType = rle ? type - 8 : type; // 1 = palette, 2 = color, 3 = gray
        if (baseType is < 1 or > 3 || mapType > 1) throw new InvalidDataException("Unsupported kind of TGA.");
        bool valid = baseType switch
        {
            1 => bits == 8 && mapType == 1 && mapBits is 15 or 16 or 24 or 32,
            2 => bits is 15 or 16 or 24 or 32,
            _ => bits is 8 or 16,
        };
        if (!valid) throw new InvalidDataException("Unsupported kind of TGA.");
        CheckSize(width, height, maxPixels);

        int bytesPerPixel = (bits + 7) / 8;
        long p = 18L + idLength;
        // The color map (also present, and skipped, in files that do not use it).
        int mapEntry = (mapBits + 7) / 8;
        long mapStart = p;
        if (mapType == 1) p += (long)mapLength * mapEntry;
        long pixels = (long)width * height;
        // What must be there: every pixel, or with run-length packets at least one per 128 pixels.
        long needed = rle ? (pixels + 127) / 128 * (1 + bytesPerPixel) : pixels * bytesPerPixel;
        if (p > d.Length || d.Length - p < needed) throw new InvalidDataException("The image data is cut short.");

        int alphaBits = descriptor & 0x0F;
        bool topFirst = (descriptor & 0x20) != 0, rightFirst = (descriptor & 0x10) != 0;
        var image = new PixelBuffer(width, height);
        byte[] o = image.Data;
        Span<byte> color = stackalloc byte[4]; // b, g, r, a

        void Read(long at, Span<byte> into)
        {
            switch (baseType)
            {
                case 1:
                    int index = d[at] - mapFirst;
                    if (index < 0 || index >= mapLength) { into[0] = into[1] = into[2] = 0; into[3] = 255; }
                    else Color(mapStart + (long)index * mapEntry, mapBits, into);
                    break;
                case 2:
                    Color(at, bits, into);
                    break;
                default:
                    into[0] = into[1] = into[2] = d[at];
                    into[3] = bits == 16 && alphaBits > 0 ? d[at + 1] : (byte)255;
                    break;
            }
        }

        void Color(long at, int depth, Span<byte> into)
        {
            if (depth is 15 or 16)
            {
                int v = d[at] | (d[at + 1] << 8);
                into[0] = (byte)((v & 0x1F) * 255 / 31);
                into[1] = (byte)(((v >> 5) & 0x1F) * 255 / 31);
                into[2] = (byte)(((v >> 10) & 0x1F) * 255 / 31);
                into[3] = depth == 16 && alphaBits == 1 ? ((v & 0x8000) != 0 ? (byte)255 : (byte)0) : (byte)255;
            }
            else
            {
                into[0] = d[at]; into[1] = d[at + 1]; into[2] = d[at + 2];
                // 32 bits with no alpha bits declared: the fourth byte is padding, not transparency.
                into[3] = depth == 32 && alphaBits > 0 ? d[at + 3] : (byte)255;
            }
        }

        void Store(long n, ReadOnlySpan<byte> c)
        {
            int x = (int)(n % width), y = (int)(n / width);
            if (rightFirst) x = width - 1 - x;
            if (!topFirst) y = height - 1 - y;
            int at = (y * width + x) * 4;
            o[at] = c[0]; o[at + 1] = c[1]; o[at + 2] = c[2]; o[at + 3] = c[3];
        }

        if (!rle)
        {
            for (long n = 0; n < pixels; n++, p += bytesPerPixel)
            {
                Read(p, color);
                Store(n, color);
            }
            return image;
        }
        for (long n = 0; n < pixels;)
        {
            // A file cut short inside its packets: what is missing stays black.
            if (p >= d.Length) break;
            int header = d[p++];
            int count = (header & 0x7F) + 1;
            if ((header & 0x80) != 0)
            {
                if (p + bytesPerPixel > d.Length) break;
                Read(p, color);
                p += bytesPerPixel;
                for (int i = 0; i < count && n < pixels; i++, n++) Store(n, color);
            }
            else
            {
                for (int i = 0; i < count && n < pixels; i++, n++)
                {
                    if (p + bytesPerPixel > d.Length) return image;
                    Read(p, color);
                    p += bytesPerPixel;
                    Store(n, color);
                }
            }
        }
        return image;
    }
}
