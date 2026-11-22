using InputFlip.Core.Ddc;

namespace InputFlip.Core.Monitors;

/// <summary>Which picture and sound controls to offer for a monitor, from its capabilities string.</summary>
public static class MonitorFeatures
{
    /// <summary>
    /// Whether to show a control for <paramref name="code"/>. Monitors that list their codes are taken at their word.
    /// Without a capabilities string, brightness and contrast are offered (nearly every DDC/CI monitor has them) and
    /// sound isn't (most monitors have no speakers).
    /// </summary>
    public static bool Offers(MonitorSnapshot monitor, byte code)
    {
        if (monitor.Health == MonitorHealth.Unavailable) return false;
        var codes = CapabilitiesParser.GetSupportedCodes(monitor.CapabilitiesText);
        if (codes.Count > 0) return codes.Contains(code);
        return code is VcpCodes.Brightness or VcpCodes.Contrast;
    }

    /// <summary>Whether a monitor has anything for the picture and sound panel.</summary>
    public static bool HasAnyLevel(MonitorSnapshot monitor) =>
        Offers(monitor, VcpCodes.Brightness) || Offers(monitor, VcpCodes.Contrast) || Offers(monitor, VcpCodes.SpeakerVolume);
}
