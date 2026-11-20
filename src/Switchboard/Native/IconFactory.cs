using System.Runtime.InteropServices;

namespace Switchboard.Native;

/// <summary>
/// Draws the tray icon (a monitor with two arrows) at the exact pixel size the taskbar wants,
/// with 4x4 supersampling for smooth edges. Avoids shipping .ico files in stage 2.
/// </summary>
internal static class IconFactory
{
    public static IntPtr CreateTrayIcon(int size, bool lightTaskbar)
    {
        // White glyph on a dark taskbar, near-black on a light one.
        byte shade = lightTaskbar ? (byte)0x1B : (byte)0xFF;
        var pixels = new uint[size * size];
        const int ss = 4;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                        if (Inside((x + (sx + 0.5) / ss) / size, (y + (sy + 0.5) / ss) / size)) hits++;
                if (hits == 0) continue;
                uint alpha = (uint)(255 * hits / (ss * ss));
                // Premultiplied BGRA.
                uint c = (uint)(shade * alpha / 255);
                pixels[y * size + x] = (alpha << 24) | (c << 16) | (c << 8) | c;
            }
        }
        return ToIcon(pixels, size);
    }

    // Shape in unit coordinates (0..1, y down).
    private static bool Inside(double x, double y)
    {
        // Screen: rounded outline.
        bool outer = RoundRect(x, y, 0.06, 0.12, 0.94, 0.70, 0.09);
        bool inner = RoundRect(x, y, 0.15, 0.21, 0.85, 0.61, 0.03);
        if (outer && !inner) return true;
        // Stand and base.
        if (x is >= 0.45 and <= 0.55 && y is >= 0.70 and <= 0.82) return true;
        if (RoundRect(x, y, 0.28, 0.81, 0.72, 0.90, 0.04)) return true;
        // Arrow to the right (upper) and to the left (lower).
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
        var bytes = new int[pixels.Length];
        Buffer.BlockCopy(pixels, 0, bytes, 0, pixels.Length * 4);
        Marshal.Copy(bytes, 0, bits, bytes.Length);

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
