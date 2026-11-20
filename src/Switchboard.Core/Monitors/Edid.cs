using System.Text;
using Microsoft.Win32;

namespace Switchboard.Core.Monitors;

/// <summary>The parts of a monitor's EDID that identify it.</summary>
public sealed record EdidInfo(string Manufacturer, ushort ProductCode, uint SerialNumber, string? Name, string? SerialText);

public static class EdidParser
{
    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    public static EdidInfo? Parse(byte[]? edid)
    {
        if (edid is null || edid.Length < 128) return null;
        for (int i = 0; i < Header.Length; i++)
            if (edid[i] != Header[i]) return null;

        // Bytes 8-9: three 5-bit letters, big-endian, 1 = 'A'.
        int m = (edid[8] << 8) | edid[9];
        var manufacturer = new string([Letter(m >> 10), Letter(m >> 5), Letter(m)]);
        ushort product = (ushort)(edid[10] | (edid[11] << 8));
        uint serial = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));

        string? name = null, serialText = null;
        for (int offset = 54; offset <= 108; offset += 18)
        {
            // Display descriptors start with 00 00 00 <tag> 00.
            if (edid[offset] != 0 || edid[offset + 1] != 0) continue;
            byte tag = edid[offset + 3];
            if (tag == 0xFC) name = DescriptorText(edid, offset + 5);
            else if (tag == 0xFF) serialText = DescriptorText(edid, offset + 5);
        }

        return new EdidInfo(manufacturer, product, serial, name, serialText);
    }

    private static char Letter(int bits) => (char)('@' + (bits & 0x1F));

    private static string? DescriptorText(byte[] edid, int start)
    {
        var sb = new StringBuilder();
        for (int i = start; i < start + 13; i++)
        {
            byte b = edid[i];
            if (b == 0x0A || b == 0x00) break;
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        string text = sb.ToString().Trim();
        return text.Length == 0 ? null : text;
    }
}

public static class EdidReader
{
    /// <summary>
    /// <c>\\?\DISPLAY#MSI3CB0#5&amp;2f7a&amp;0&amp;UID4353#{e6f07b5f-...}</c> becomes <c>DISPLAY\MSI3CB0\5&amp;2f7a&amp;0&amp;UID4353</c>.
    /// </summary>
    public static string? DeviceInterfaceToInstanceId(string? deviceInterface)
    {
        if (string.IsNullOrWhiteSpace(deviceInterface)) return null;
        string id = deviceInterface;
        if (id.StartsWith(@"\\?\", StringComparison.Ordinal)) id = id.Substring(4);
        int brace = id.IndexOf("#{", StringComparison.Ordinal);
        if (brace >= 0) id = id.Substring(0, brace);
        id = id.Replace('#', '\\');
        return id.Length == 0 ? null : id;
    }

    /// <summary>Reads the EDID Windows cached in the registry for this monitor, or null.</summary>
    public static EdidInfo? TryRead(string? deviceInterface)
    {
        string? instanceId = DeviceInterfaceToInstanceId(deviceInterface);
        if (instanceId is null) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters");
            return EdidParser.Parse(key?.GetValue("EDID") as byte[]);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read the EDID for {instanceId}: {ex.Message}");
            return null;
        }
    }
}
