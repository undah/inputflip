using System.Buffers.Binary;
using System.IO.Compression;

namespace Switchboard.Core.Branding;

/// <summary>
/// The app icon (a white monitor with two arrows on a blue rounded square), drawn at any pixel size with
/// 4x4 supersampling. Used for the window icon at run time and to write Assets/Switchboard.ico.
/// </summary>
public static class IconArt
{
    /// <summary>Sizes in the .ico file: taskbar, title bar, Explorer and Start at 100% to 300% scaling.</summary>
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    // Windows blue, the same in light and dark mode, so the icon reads on any taskbar.
    private const byte BgR = 0x00, BgG = 0x67, BgB = 0xC0;

    /// <summary>Premultiplied BGRA pixels, top row first, as 0xAARRGGBB.</summary>
    public static uint[] Render(int size)
    {
        var pixels = new uint[size * size];
        const int ss = 4;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int bg = 0, fg = 0;
                for (int sy = 0; sy < ss; sy++)
                {
                    for (int sx = 0; sx < ss; sx++)
                    {
                        double u = (x + (sx + 0.5) / ss) / size, v = (y + (sy + 0.5) / ss) / size;
                        if (!RoundRect(u, v, 0.02, 0.02, 0.98, 0.98, 0.22)) continue;
                        bg++;
                        // The glyph sits inside the square at 70% size.
                        if (Glyph((u - 0.15) / 0.7, (v - 0.15) / 0.7)) fg++;
                    }
                }
                if (bg == 0) continue;
                const int total = ss * ss;
                uint alpha = (uint)(255 * bg / total);
                // Blend the white glyph over the blue within the covered area, then premultiply.
                double f = (double)fg / bg;
                uint r = (uint)((BgR + (255 - BgR) * f) * alpha / 255);
                uint g = (uint)((BgG + (255 - BgG) * f) * alpha / 255);
                uint b = (uint)((BgB + (255 - BgB) * f) * alpha / 255);
                pixels[y * size + x] = (alpha << 24) | (r << 16) | (g << 8) | b;
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

    // Monitor with two arrows, in unit coordinates (0..1, y down).
    private static bool Glyph(double x, double y)
    {
        bool outer = RoundRect(x, y, 0.06, 0.12, 0.94, 0.70, 0.09);
        bool inner = RoundRect(x, y, 0.15, 0.21, 0.85, 0.61, 0.03);
        if (outer && !inner) return true;
        if (x is >= 0.45 and <= 0.55 && y is >= 0.70 and <= 0.82) return true;
        if (RoundRect(x, y, 0.28, 0.81, 0.72, 0.90, 0.04)) return true;
        if (x is >= 0.30 and <= 0.60 && y is >= 0.315 and <= 0.375) return true;
        if (Triangle(x, y, 0.58, 0.27, 0.58, 0.42, 0.72, 0.345)) return true;
        if (x is >= 0.40 and <= 0.70 && y is >= 0.455 and <= 0.515) return true;
        if (Triangle(x, y, 0.42, 0.41, 0.42, 0.56, 0.28, 0.485)) return true;
        return false;
    }

    private static bool RoundRect(double x, double y, double l, double t, double r, double b, double radius)
    {
        if (x < l || x > r || y < t || y > b) return false;
        double cx = Math.Clamp(x, l + radius, r - radius);
        double cy = Math.Clamp(y, t + radius, b - radius);
        double dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static bool Triangle(double x, double y, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        double d1 = (x - x2) * (y1 - y2) - (x1 - x2) * (y - y2);
        double d2 = (x - x3) * (y2 - y3) - (x2 - x3) * (y - y3);
        double d3 = (x - x1) * (y3 - y1) - (x3 - x1) * (y - y1);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
}
