using System.Runtime.InteropServices;

namespace Switchboard.Native;

/// <summary>
/// Draws the app icon (a white monitor with two arrows on a blue rounded square) at any pixel size,
/// with 4x4 supersampling for smooth edges. Avoids shipping .ico files until stage 3.
/// </summary>
internal static class IconFactory
{
    // Windows blue, the same in light and dark mode, so the icon reads on any taskbar.
    private const byte BgR = 0x00, BgG = 0x67, BgB = 0xC0;

    public static IntPtr CreateAppIcon(int size)
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
                // Blend white glyph over the blue within the covered area, then premultiply.
                double f = bg == 0 ? 0 : (double)fg / bg;
                uint r = (uint)((BgR + (255 - BgR) * f) * alpha / 255);
                uint g = (uint)((BgG + (255 - BgG) * f) * alpha / 255);
                uint b = (uint)((BgB + (255 - BgB) * f) * alpha / 255);
                pixels[y * size + x] = (alpha << 24) | (r << 16) | (g << 8) | b;
            }
        }
        return ToIcon(pixels, size);
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

    private static IntPtr ToIcon(uint[] pixels, int size)
    {
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr color = Win32.CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        var ints = new int[pixels.Length];
        Buffer.BlockCopy(pixels, 0, ints, 0, pixels.Length * 4);
        Marshal.Copy(ints, 0, bits, ints.Length);

        // All-zero mask: the alpha channel does the work. Monochrome rows are padded to 16 bits.
        var maskBits = new byte[(size + 15) / 16 * 2 * size];
        IntPtr mask;
        unsafe
        {
            fixed (byte* p = maskBits) mask = Win32.CreateBitmap(size, size, 1, 1, (IntPtr)p);
        }
        var info = new Win32.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        IntPtr icon = Win32.CreateIconIndirect(ref info);
        Win32.DeleteObject(color);
        Win32.DeleteObject(mask);
        return icon;
    }
}
