using System.Buffers.Binary;
using System.IO.Compression;

namespace InputFlip.Core.Branding;

/// <summary>
/// The app icon: two tiles on a blue rounded square, a pale one behind and a white one flipped to the front, like the
/// input tiles in the window. Drawn at any pixel size with 4x4 supersampling. Used for the window and tray icon at run
/// time and to write Assets/InputFlip.ico.
/// </summary>
public static class IconArt
{
    /// <summary>Sizes in the .ico file: taskbar, title bar, Explorer and Start at 100% to 300% scaling.</summary>
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    // The app's blue (#2563EB, the input showing now), the same in light and dark mode, so it reads on any taskbar.
    private static readonly (int R, int G, int B) Background = (0x25, 0x63, 0xEB);
    private static readonly (int R, int G, int B) BackTile = (0xA9, 0xC4, 0xFA);
    private static readonly (int R, int G, int B) FrontTile = (0xFF, 0xFF, 0xFF);

    /// <summary>Premultiplied BGRA pixels, top row first, as 0xAARRGGBB.</summary>
    public static uint[] Render(int size)
    {
        var pixels = new uint[size * size];
        const int ss = 4;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int covered = 0, r = 0, g = 0, b = 0;
                for (int sy = 0; sy < ss; sy++)
                {
                    for (int sx = 0; sx < ss; sx++)
                    {
                        double u = (x + (sx + 0.5) / ss) / size, v = (y + (sy + 0.5) / ss) / size;
                        if (!RoundRect(u, v, 0.02, 0.02, 0.98, 0.98, 0.22)) continue;
                        covered++;
                        // The tiles sit inside the square at 76% size.
                        var c = Glyph((u - 0.12) / 0.76, (v - 0.12) / 0.76) switch
                        {
                            2 => FrontTile,
                            1 => BackTile,
                            _ => Background,
                        };
                        r += c.R;
                        g += c.G;
                        b += c.B;
                    }
                }
                if (covered == 0) continue;
                const int total = ss * ss;
                uint alpha = (uint)(255 * covered / total);
                // Average the covered samples, then premultiply by the coverage.
                uint pr = (uint)(r / covered * alpha / 255), pg = (uint)(g / covered * alpha / 255), pb = (uint)(b / covered * alpha / 255);
                pixels[y * size + x] = (alpha << 24) | (pr << 16) | (pg << 8) | pb;
            }
        }
        return pixels;
    }

    /// <summary>Writes a .ico with one PNG image per size (Windows Vista and later read PNG icons).</summary>
    public static byte[] CreateIco(IReadOnlyList<int> sizes)
    {
        var images = sizes.Select(size => (size, png: EncodePng(Render(size), size))).ToList();
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0); // reserved
        w.Write((ushort)1); // type: icon
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); // palette
            w.Write((byte)0); // reserved
            w.Write((ushort)1); // planes
            w.Write((ushort)32); // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Minimal PNG encoder: 8-bit RGBA, no filtering.</summary>
    public static byte[] EncodePng(uint[] premultiplied, int size)
    {
        var raw = new byte[size * (size * 4 + 1)];
        int at = 0;
        for (int y = 0; y < size; y++)
        {
            raw[at++] = 0; // filter: none
            for (int x = 0; x < size; x++)
            {
                uint p = premultiplied[y * size + x];
                uint a = p >> 24;
                // PNG wants straight (not premultiplied) alpha.
                byte Un(uint c) => a == 0 ? (byte)0 : (byte)Math.Min(255, (c * 255 + a / 2) / a);
                raw[at++] = Un((p >> 16) & 0xFF);
                raw[at++] = Un((p >> 8) & 0xFF);
                raw[at++] = Un(p & 0xFF);
                raw[at++] = (byte)a;
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, size);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), size);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc;
    }

    // Two tiles in unit coordinates (0..1, y down): 0 = background, 1 = the pale tile behind, 2 = the white tile in front.
    // A ring of background around the front tile keeps the two apart at 16 px.
    private static int Glyph(double x, double y)
    {
        if (RoundRect(x, y, 0.36, 0.36, 0.96, 0.96, 0.14)) return 2;
        if (RoundRect(x, y, 0.28, 0.28, 1.04, 1.04, 0.20)) return 0;
        if (RoundRect(x, y, 0.04, 0.04, 0.64, 0.64, 0.14)) return 1;
        return 0;
    }

    private static bool RoundRect(double x, double y, double l, double t, double r, double b, double radius)
    {
        if (x < l || x > r || y < t || y > b) return false;
        double cx = Math.Clamp(x, l + radius, r - radius);
        double cy = Math.Clamp(y, t + radius, b - radius);
        double dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }
}
