using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switchboard.Core.Settings;

/// <summary>
/// Everything saved in settings.json. The file is meant to be edited by hand until stage 3 adds a settings window,
/// so names are plain and input values are written as hex strings ("0x11") to match the docs.
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>Brings the window to the front. Default avoids Ctrl+Alt+letter, which is AltGr on many keyboard layouts.</summary>
    public string? ShowHotkey { get; set; } = "Ctrl+Alt+Shift+S";

    /// <summary>Switches <see cref="CycleMonitor"/> to its next visible input.</summary>
    public string? CycleHotkey { get; set; } = "Ctrl+Alt+Shift+I";

    /// <summary>Monitor id the cycle shortcut acts on. Empty means the main display, or the first monitor that answers.</summary>
    public string? CycleMonitor { get; set; }

    /// <summary>Keep the window above other windows.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>Where the window was last placed, in physical screen pixels. Empty: on a monitor other than the main one.</summary>
    public WindowPlacement? WindowPosition { get; set; }

    /// <summary>Keyed by monitor id (see MonitorIdentity).</summary>
    public Dictionary<string, MonitorSettings> Monitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MonitorSettings
{
    /// <summary>Name shown in the app. Starts as the model name.</summary>
    public string? Nickname { get; set; }

    /// <summary>Leave this monitor out of the app.</summary>
    public bool Hidden { get; set; }

    /// <summary>Inputs in display order. Filled from the monitor's capabilities string; you can add inputs by value.</summary>
    public List<InputSettings> Inputs { get; set; } = new();
}

public sealed class InputSettings
{
    [JsonConverter(typeof(HexValueConverter))]
    public uint Value { get; set; }

    /// <summary>Your name for what's plugged in, e.g. "PS5". Empty shows the port name.</summary>
    public string? Name { get; set; }

    /// <summary>pc, laptop, console, tv or display.</summary>
    public string? Icon { get; set; }

    public bool Hidden { get; set; }

    /// <summary>Switch straight to this input, e.g. "Ctrl+Alt+Shift+1".</summary>
    public string? Hotkey { get; set; }
}

public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }
}

/// <summary>Reads 17, "17", "0x11" or "11h"; writes "0x11".</summary>
public sealed class HexValueConverter : JsonConverter<uint>
{
    public override uint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetUInt32();
        if (reader.TokenType == JsonTokenType.String && TryParse(reader.GetString(), out uint value)) return value;
        throw new JsonException("An input value must be a number such as \"0x11\".");
    }

    public override void Write(Utf8JsonWriter writer, uint value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value > 0xFF ? $"0x{value:X4}" : $"0x{value:X2}");

    public static bool TryParse(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        if (text.EndsWith('h') || text.EndsWith('H'))
            return uint.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
