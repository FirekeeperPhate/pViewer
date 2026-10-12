using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pViewer.Imaging;

/// <summary>
/// Writes a still GIF. A GIF holds at most 256 colors, one of which may be "transparent": an image
/// with few colors is written exactly as it is; for the others a palette is chosen for the image
/// and what each pixel is off by is spread over its neighbours (dithering). The GIF encoder of
/// Windows is not used: it leaves transparency out and takes a fixed palette.
/// </summary>
internal static class GifWriter
{
    public static void Write(BitmapSource bitmap, Stream output)
    {
        var pixels = ImageBridge.ToPixels(bitmap);
        int width = pixels.Width, height = pixels.Height;
        if (width > ushort.MaxValue || height > ushort.MaxValue)
            throw new NotSupportedException($"A GIF cannot be larger than 65535 pixels ({width} × {height}).");
        byte[] d = pixels.Data;
        // GIF transparency is on or off.
        bool anyTransparent = false;
        for (int i = 3; i < d.Length; i += 4)
        {
            if (d[i] < 128) { d[i - 3] = d[i - 2] = d[i - 1] = d[i] = 0; anyTransparent = true; }
            else d[i] = 255;
        }

        var indices = new byte[width * height];
        List<Color> palette = ExactPalette(d, indices, anyTransparent) ?? ReducedPalette(pixels, indices, anyTransparent);
        int transparent = -1;
        if (anyTransparent)
        {
            transparent = palette.Count;
            palette.Add(Colors.Black);
            for (int i = 0; i < indices.Length; i++)
                if (d[i * 4 + 3] == 0) indices[i] = (byte)transparent;
        }

        // The color table holds 2, 4, 8 … 256 entries.
        int bits = 1;
        while ((1 << bits) < palette.Count) bits++;
        var w = new BinaryWriter(output);
        w.Write("GIF89a"u8);
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write((byte)(0x80 | ((bits - 1) << 4) | (bits - 1))); // a color table for the whole file follows
        w.Write((byte)0); // background color
        w.Write((byte)0); // pixels are square
        for (int i = 0; i < 1 << bits; i++)
        {
            var c = i < palette.Count ? palette[i] : Colors.Black;
            w.Write(c.R); w.Write(c.G); w.Write(c.B);
        }
        if (transparent >= 0)
        {
            w.Write([0x21, 0xF9, 4, 0x01, 0, 0, (byte)transparent, 0]); // graphic control: this index is see-through
        }
        w.Write((byte)0x2C);
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write((ushort)width); w.Write((ushort)height);
        w.Write((byte)0); // no table of its own, rows in order
        WriteLzw(w, indices, Math.Max(2, bits));
        w.Write((byte)0x3B);
        w.Flush();
    }

    /// <summary>The colors of the image as they are, if they fit; null otherwise.</summary>
    private static List<Color>? ExactPalette(byte[] d, byte[] indices, bool anyTransparent)
    {
        int room = anyTransparent ? 255 : 256;
        var known = new Dictionary<int, byte>();
        var palette = new List<Color>();
        for (int i = 0, n = 0; i < d.Length; i += 4, n++)
        {
            if (d[i + 3] == 0) continue;
            int key = d[i] | (d[i + 1] << 8) | (d[i + 2] << 16);
            if (!known.TryGetValue(key, out byte index))
            {
                if (palette.Count == room) return null;
                index = (byte)palette.Count;
                known[key] = index;
                palette.Add(Color.FromRgb(d[i + 2], d[i + 1], d[i]));
            }
            indices[n] = index;
        }
        if (palette.Count == 0) palette.Add(Colors.Black); // only transparent pixels
        return palette;
    }

    /// <summary>
    /// More colors than a GIF can hold: a palette chosen for this image. The colors are counted in
    /// a coarse cube (32 steps per channel), the cube is cut again and again where the colors
    /// differ most until there is one box per palette entry, and the entries are then moved to the
    /// middle of the colors nearest to them. The pixels get the nearest entry, and what each one
    /// is off by is passed on to its neighbours (Floyd-Steinberg), so gradients stay smooth.
    /// </summary>
    private static List<Color> ReducedPalette(PixelBuffer pixels, byte[] indices, bool anyTransparent)
    {
        byte[] d = pixels.Data;
        const int Bins = 32 * 32 * 32;
        var count = new long[Bins];
        var sumR = new long[Bins];
        var sumG = new long[Bins];
        var sumB = new long[Bins];
        for (int i = 0; i < d.Length; i += 4)
        {
            if (d[i + 3] == 0) continue;
            int bin = ((d[i + 2] >> 3) << 10) | ((d[i + 1] >> 3) << 5) | (d[i] >> 3);
            count[bin]++;
            sumR[bin] += d[i + 2]; sumG[bin] += d[i + 1]; sumB[bin] += d[i];
        }
        int[] used = [.. Enumerable.Range(0, Bins).Where(b => count[b] > 0)];
        // The color of a bin on one axis (0 red, 1 green, 2 blue): the average of what fell into it.
        double Value(int bin, int axis) => (axis == 0 ? sumR[bin] : axis == 1 ? sumG[bin] : sumB[bin]) / (double)count[bin];

        // Boxes are stretches of "used": (start, length). Cut the one whose colors are spread the most.
        int wanted = anyTransparent ? 255 : 256;
        var boxes = new List<(int Start, int Length)> { (0, used.Length) };
        (double Spread, int Axis) Measure((int Start, int Length) box)
        {
            double best = -1;
            int bestAxis = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double n = 0, sum = 0, squares = 0;
                for (int k = box.Start; k < box.Start + box.Length; k++)
                {
                    double v = Value(used[k], axis), c = count[used[k]];
                    n += c; sum += v * c; squares += v * v * c;
                }
                double spread = squares - sum * sum / n; // variance × pixels
                if (spread > best) (best, bestAxis) = (spread, axis);
            }
            return (best, bestAxis);
        }
        var measures = new List<(double Spread, int Axis)> { Measure(boxes[0]) };
        while (boxes.Count < wanted)
        {
            int widest = -1;
            for (int b = 0; b < boxes.Count; b++)
                if (boxes[b].Length > 1 && (widest < 0 || measures[b].Spread > measures[widest].Spread)) widest = b;
            if (widest < 0) break;
            var (start, length) = boxes[widest];
            int axis = measures[widest].Axis;
            Array.Sort(used, start, length, Comparer<int>.Create((a, b) => Value(a, axis).CompareTo(Value(b, axis))));
            // Half of the pixels on each side (at least one bin on each).
            long total = 0, half = 0;
            for (int k = start; k < start + length; k++) total += count[used[k]];
            int cut = start + 1;
            for (int k = start; k < start + length - 1; k++)
            {
                half += count[used[k]];
                cut = k + 1;
                if (half * 2 >= total) break;
            }
            boxes[widest] = (start, cut - start);
            boxes.Add((cut, start + length - cut));
            measures[widest] = Measure(boxes[widest]);
            measures.Add(Measure(boxes[^1]));
        }

        var r = new double[boxes.Count];
        var g = new double[boxes.Count];
        var bl = new double[boxes.Count];
        var owner = new int[Bins];
        for (int b = 0; b < boxes.Count; b++)
            for (int k = boxes[b].Start; k < boxes[b].Start + boxes[b].Length; k++) owner[used[k]] = b;
        // Each entry to the average of its colors, then the colors to their nearest entry, a few times.
        for (int round = 0; round < 4; round++)
        {
            var n = new long[boxes.Count];
            var tr = new long[boxes.Count];
            var tg = new long[boxes.Count];
            var tb = new long[boxes.Count];
            foreach (int bin in used)
            {
                int o = owner[bin];
                n[o] += count[bin]; tr[o] += sumR[bin]; tg[o] += sumG[bin]; tb[o] += sumB[bin];
            }
            for (int b = 0; b < boxes.Count; b++)
                if (n[b] > 0) (r[b], g[b], bl[b]) = (tr[b] / (double)n[b], tg[b] / (double)n[b], tb[b] / (double)n[b]);
            if (round == 3) break;
            foreach (int bin in used)
            {
                double vr = Value(bin, 0), vg = Value(bin, 1), vb = Value(bin, 2), best = double.MaxValue;
                for (int b = 0; b < boxes.Count; b++)
                {
                    double distance = (vr - r[b]) * (vr - r[b]) + (vg - g[b]) * (vg - g[b]) + (vb - bl[b]) * (vb - bl[b]);
                    if (distance < best) (best, owner[bin]) = (distance, b);
                }
            }
        }
        var palette = new List<Color>(boxes.Count + 1);
        for (int b = 0; b < boxes.Count; b++)
            palette.Add(Color.FromRgb((byte)Math.Round(r[b]), (byte)Math.Round(g[b]), (byte)Math.Round(bl[b])));

        // Nearest entry of a color, remembered per coarse color (found the first time it is asked for).
        var nearest = new short[Bins];
        Array.Fill(nearest, (short)-1);
        int Nearest(int red, int green, int blue)
        {
            int bin = ((red >> 3) << 10) | ((green >> 3) << 5) | (blue >> 3);
            if (nearest[bin] >= 0) return nearest[bin];
            int cr = (red & 0xF8) | 4, cg = (green & 0xF8) | 4, cb = (blue & 0xF8) | 4, best = int.MaxValue, index = 0;
            for (int p = 0; p < palette.Count; p++)
            {
                int dr = cr - palette[p].R, dg = cg - palette[p].G, db = cb - palette[p].B;
                int distance = dr * dr + dg * dg + db * db;
                if (distance < best) (best, index) = (distance, p);
            }
            nearest[bin] = (short)index;
            return index;
        }

        int width = pixels.Width, height = pixels.Height;
        // What the pixels of this row and of the next still owe, in sixteenths of a color step.
        var here = new int[(width + 2) * 3];
        var below = new int[(width + 2) * 3];
        for (int y = 0; y < height; y++)
        {
            Array.Clear(below);
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4, e = (x + 1) * 3;
                if (d[i + 3] == 0) continue; // transparent: gets its own index, owes and passes on nothing
                int red = Math.Clamp(d[i + 2] + (here[e] + 8) / 16, 0, 255);
                int green = Math.Clamp(d[i + 1] + (here[e + 1] + 8) / 16, 0, 255);
                int blue = Math.Clamp(d[i] + (here[e + 2] + 8) / 16, 0, 255);
                int index = Nearest(red, green, blue);
                indices[y * width + x] = (byte)index;
                var chosen = palette[index];
                int er = red - chosen.R, eg = green - chosen.G, eb = blue - chosen.B;
                // 7/16 to the right, 3/16 below left, 5/16 below, 1/16 below right.
                here[e + 3] += er * 7; here[e + 4] += eg * 7; here[e + 5] += eb * 7;
                below[e - 3] += er * 3; below[e - 2] += eg * 3; below[e - 1] += eb * 3;
                below[e] += er * 5; below[e + 1] += eg * 5; below[e + 2] += eb * 5;
                below[e + 3] += er; below[e + 4] += eg; below[e + 5] += eb;
            }
            (here, below) = (below, here);
        }
        return palette;
    }

    /// <summary>The compression every GIF uses (LZW with codes that grow from codeSize + 1 to 12 bits).</summary>
    private static void WriteLzw(BinaryWriter w, byte[] indices, int codeSize)
    {
        w.Write((byte)codeSize);
        var block = new byte[255];
        int blockLength = 0;
        uint bitBuffer = 0;
        int bitCount = 0;
        int clear = 1 << codeSize, end = clear + 1;
        int next = end + 1, width = codeSize + 1;
        var table = new Dictionary<int, int>(4096); // (code of the sequence so far, next byte) -> code of the longer sequence

        void Emit(int code)
        {
            bitBuffer |= (uint)code << bitCount;
            bitCount += width;
            while (bitCount >= 8)
            {
                block[blockLength++] = (byte)bitBuffer;
                bitBuffer >>= 8;
                bitCount -= 8;
                if (blockLength == 255) FlushBlock();
            }
        }

        void FlushBlock()
        {
            if (blockLength == 0) return;
            w.Write((byte)blockLength);
            w.Write(block, 0, blockLength);
            blockLength = 0;
        }

        Emit(clear);
        if (indices.Length > 0)
        {
            int current = indices[0];
            for (int i = 1; i < indices.Length; i++)
            {
                int value = indices[i];
                int key = (current << 8) | value;
                if (table.TryGetValue(key, out int longer))
                {
                    current = longer;
                    continue;
                }
                Emit(current);
                if (next < 4096)
                {
                    table[key] = next++;
                    // The reader makes its codes one bit wider at the same moment.
                    if (next > (1 << width) && width < 12) width++;
                }
                else
                {
                    Emit(clear);
                    table.Clear();
                    next = end + 1;
                    width = codeSize + 1;
                }
                current = value;
            }
            Emit(current);
            // A reader adds one more sequence after that last code, and may widen its codes for it.
            if (next == (1 << width) && width < 12) width++;
        }
        Emit(end);
        if (bitCount > 0) block[blockLength++] = (byte)bitBuffer;
        FlushBlock();
        w.Write((byte)0); // end of the image data
    }
}
