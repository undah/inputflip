namespace Switchboard.Core.Ddc;

public static class VcpCodes
{
    public const byte InputSource = 0x60;
}

/// <summary>Friendly names for VCP 0x60 input values (MCCS standard, plus the common USB-C value).</summary>
public static class InputCatalog
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x01] = "VGA 1", [0x02] = "VGA 2",
        [0x03] = "DVI 1", [0x04] = "DVI 2",
        [0x05] = "Composite 1", [0x06] = "Composite 2",
        [0x07] = "S-Video 1", [0x08] = "S-Video 2",
        [0x09] = "Tuner 1", [0x0A] = "Tuner 2", [0x0B] = "Tuner 3",
        [0x0C] = "Component 1", [0x0D] = "Component 2", [0x0E] = "Component 3",
        [0x0F] = "DisplayPort 1", [0x10] = "DisplayPort 2",
        [0x11] = "HDMI 1", [0x12] = "HDMI 2",
        [0x1B] = "USB-C",
    };

    /// <summary>
    /// Shown when a monitor's capabilities string can't be read and nothing is in settings yet.
    /// The user can hide the ones that don't exist.
    /// </summary>
    public static IReadOnlyList<uint> FallbackInputs { get; } = [0x0F, 0x10, 0x11, 0x12];

    /// <summary>"HDMI 1", "DisplayPort 1", or "Input 0x1F" for values outside the table.</summary>
    public static string GetPortName(uint value)
    {
        if (Names.TryGetValue(value, out var name)) return name;
        // Some monitors put the standard value in the low byte and something vendor-specific in the high byte.
        if (value > 0xFF && Names.TryGetValue(value & 0xFF, out var low)) return low;
        return $"Input {FormatValue(value)}";
    }

    public static string FormatValue(uint value) => value > 0xFF ? $"0x{value:X4}" : $"0x{value:X2}";

    /// <summary>
    /// Whether a value read back from the monitor means it's on <paramref name="target"/>.
    /// Some monitors report extra bits in the high byte, so for standard one-byte targets only the low byte counts.
    /// </summary>
    public static bool Matches(uint reported, uint target)
    {
        if (reported == target) return true;
        return target <= 0xFF && (reported & 0xFF) == target;
    }
}
