using System.Text.RegularExpressions;

namespace Switchboard.Core.Usb;

/// <summary>
/// A USB device as rules know it: vendor and product id, and the serial when the device has one
/// ("VID_046D&amp;PID_C52B\5&amp;1A2B3C4D&amp;0&amp;3" from Windows, stored as "VID_046D&amp;PID_C52B").
/// </summary>
public sealed partial record UsbDeviceId(ushort VendorId, ushort ProductId, string? Serial)
{
    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();

    /// <summary>
    /// Reads a device interface path (<c>\\?\USB#VID_046D&amp;PID_C52B#serial#{a5dcbf10-...}</c>), a device instance id
    /// (<c>USB\VID_046D&amp;PID_C52B\serial</c>) or a rule's text (<c>VID_046D&amp;PID_C52B</c>, optionally <c>\serial</c>).
    /// Interface ids (<c>&amp;MI_00</c>) are ignored, so every interface of one device gives the same id.
    /// </summary>
    public static UsbDeviceId? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = VidPid().Match(text);
        if (!m.Success) return null;
        ushort vid = Convert.ToUInt16(m.Groups[1].Value, 16);
        ushort pid = Convert.ToUInt16(m.Groups[2].Value, 16);

        // What follows the VID/PID part, up to the interface-class GUID: "&MI_00" (an interface), then "#serial" or "\serial".
        string rest = text[(m.Index + m.Length)..];
        int guid = rest.IndexOf("#{", StringComparison.Ordinal);
        if (guid >= 0) rest = rest[..guid];
        int sep = rest.IndexOfAny(['#', '\\']);
        string? serial = sep >= 0 ? rest[(sep + 1)..].Trim() : null;
        // Windows makes up serials containing '&' for devices without one; they change from port to port, so don't use them.
        if (string.IsNullOrEmpty(serial) || serial.Contains('&')) serial = null;
        return new UsbDeviceId(vid, pid, serial);
    }

    /// <summary>Whether a device that plugged in is the one a rule names. A rule without a serial matches any serial.</summary>
    public bool Matches(UsbDeviceId device) =>
        VendorId == device.VendorId && ProductId == device.ProductId
        && (Serial is null || string.Equals(Serial, device.Serial, StringComparison.OrdinalIgnoreCase));

    /// <summary>The form saved in rules: "VID_046D&amp;PID_C52B" or "VID_046D&amp;PID_C52B\serial".</summary>
    public override string ToString() => Serial is null ? $"VID_{VendorId:X4}&PID_{ProductId:X4}" : $"VID_{VendorId:X4}&PID_{ProductId:X4}\\{Serial}";
}

/// <summary>A USB device that's plugged in now, for the rule editor.</summary>
public sealed record UsbDeviceInfo(UsbDeviceId Id, string Name);
