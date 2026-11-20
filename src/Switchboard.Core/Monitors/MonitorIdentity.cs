namespace Switchboard.Core.Monitors;

/// <summary>
/// Stable ids for monitors, from their EDID, so settings follow the monitor rather than the port order.
/// </summary>
public static class MonitorIdentity
{
    /// <summary>
    /// "MSI3CB0-1234ABCD" from manufacturer, product code and serial; just "MSI3CB0" if the EDID has no serial.
    /// Falls back to the Windows display name when there's no EDID (less stable, but better than nothing).
    /// </summary>
    public static string MakeId(EdidInfo? edid, string gdiDevice)
    {
        if (edid is null)
            return "display-" + Sanitize(gdiDevice.Replace(@"\\.\", "", StringComparison.Ordinal));

        string id = $"{edid.Manufacturer}{edid.ProductCode:X4}";
        if (!string.IsNullOrEmpty(edid.SerialText)) return $"{id}-{Sanitize(edid.SerialText)}";
        if (edid.SerialNumber != 0) return $"{id}-{edid.SerialNumber:X8}";
        return id;
    }

    /// <summary>Makes ids unique when two monitors report the same EDID identity (identical models without serials).</summary>
    public static string MakeUnique(string id, ISet<string> taken)
    {
        if (taken.Add(id)) return id;
        for (int n = 2; ; n++)
        {
            string candidate = $"{id}#{n}";
            if (taken.Add(candidate)) return candidate;
        }
    }

    public static string MakeName(EdidInfo? edid, string? capabilitiesModel, string? description)
    {
        if (!string.IsNullOrWhiteSpace(edid?.Name)) return edid.Name!;
        if (!string.IsNullOrWhiteSpace(capabilitiesModel)) return capabilitiesModel!;
        if (!string.IsNullOrWhiteSpace(description) && !description.Contains("Generic", StringComparison.OrdinalIgnoreCase))
            return description!;
        if (edid is not null) return $"{edid.Manufacturer} monitor";
        return "Monitor";
    }

    private static string Sanitize(string text)
    {
        var chars = text.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}
