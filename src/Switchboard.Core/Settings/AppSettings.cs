using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switchboard.Core.Settings;

/// <summary>
/// Everything saved in settings.json. The settings window edits it, and it stays friendly to edit by hand:
/// names are plain and input values are written as hex strings ("0x11") to match the docs.
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

    /// <summary>"light", "dark", or empty to follow Windows.</summary>
    public string? Theme { get; set; }

    /// <summary>Keep the window above other windows.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>
    /// Don't move the mouse when a monitor switches. Normally, when a monitor switches from this PC to another device
    /// while the mouse is on it, the mouse moves to InputFlip's window, and back when you switch to this PC again.
    /// (Stored as the opt-out because settings.json leaves out false values.)
    /// </summary>
    public bool LeaveMouseAlone { get; set; }

    /// <summary>Where the window was last placed, in physical screen pixels. Empty: on a monitor other than the main one.</summary>
    public WindowPlacement? WindowPosition { get; set; }

    /// <summary>
    /// Where the update button looks for new versions: a folder (or web address) with Velopack releases.
    /// Empty: the releases folder the installer was built in.
    /// </summary>
    public string? UpdateSource { get; set; }

    /// <summary>Opens the pop-up picker next to the mouse. Empty: no shortcut.</summary>
    public string? PickerHotkey { get; set; } = "Ctrl+Alt+Shift+P";

    /// <summary>
    /// Don't show the short "MSI → PS5" note after a switch from a shortcut, a scene, a rule or the command line.
    /// (Opt-out, like <see cref="LeaveMouseAlone"/>.)
    /// </summary>
    public bool HideToasts { get; set; }

    /// <summary>No icon by the clock. (Opt-out: the icon shows by default.)</summary>
    public bool HideTrayIcon { get; set; }

    /// <summary>Closing the window keeps InputFlip running in the tray instead of quitting.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Named sets of switches (and brightness levels) run with one click, key or command.</summary>
    public List<SceneSettings> Scenes { get; set; } = new();

    /// <summary>What to do when a USB device plugs in or leaves.</summary>
    public List<UsbRule> UsbRules { get; set; } = new();

    /// <summary>What to do when the PC locks, unlocks, sleeps or wakes. Off unless set.</summary>
    public PowerRules? PowerRules { get; set; }

    /// <summary>Keyed by monitor id (see MonitorIdentity). Monitors show in the order they're listed here.</summary>
    public Dictionary<string, MonitorSettings> Monitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A scene: switch several monitors (and set their brightness) at once.</summary>
public sealed class SceneSettings
{
    /// <summary>Stable key for shortcuts, links and rules ("console"); made from the name when the scene is created.</summary>
    public string Id { get; set; } = "";

    public string? Name { get; set; }

    /// <summary>pc, laptop, console, tv or display.</summary>
    public string? Icon { get; set; }

    public string? Hotkey { get; set; }

    public List<SceneStep> Steps { get; set; } = new();
}

/// <summary>One monitor in a scene: the input to switch to, a brightness level, or both.</summary>
public sealed class SceneStep
{
    /// <summary>Monitor id (see MonitorIdentity).</summary>
    public string Monitor { get; set; } = "";

    [JsonConverter(typeof(NullableHexValueConverter))]
    public uint? Input { get; set; }

    /// <summary>0 to 100. Empty: leave it.</summary>
    public int? Brightness { get; set; }
}

/// <summary>A USB device that switches monitors when it plugs in or leaves (a USB switch box moving the keyboard, say).</summary>
public sealed class UsbRule
{
    /// <summary>"VID_046D&amp;PID_C52B", optionally with "\serial". See <see cref="Usb.UsbDeviceId"/>.</summary>
    public string Device { get; set; } = "";

    /// <summary>What Windows called the device when the rule was made, for the settings window.</summary>
    public string? Name { get; set; }

    /// <summary>An action (see <see cref="Actions.AppAction"/>): "scene:console", "switch:MSI3CB0-000000D1:0x11" or "back".</summary>
    public string? WhenConnected { get; set; }

    public string? WhenDisconnected { get; set; }

    public bool Disabled { get; set; }
}

/// <summary>Actions for the PC locking, unlocking, going to sleep and waking. Each is an <see cref="Actions.AppAction"/>.</summary>
public sealed class PowerRules
{
    public string? OnLock { get; set; }

    public string? OnUnlock { get; set; }

    public string? OnSleep { get; set; }

    public string? OnWake { get; set; }
}

public sealed class MonitorSettings
{
    /// <summary>Name shown in the app. Starts as the model name.</summary>
    public string? Nickname { get; set; }

    /// <summary>Leave this monitor out of the app.</summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// The input this PC is on, for monitors that ignore switch commands while showing it (the MSI G27CQ4
    /// over DisplayPort). Switching from it to anything else first turns the screens off for a few seconds,
    /// which makes the monitor listen. Empty: switch normally.
    /// </summary>
    [JsonConverter(typeof(NullableHexValueConverter))]
    public uint? BlankScreensWhenLeaving { get; set; }

    /// <summary>
    /// The input this PC is plugged into, tagged "This PC" in the app. Found from the cable type Windows reports
    /// (see PcInputDetector) and remembered, or set from an input's menu. Empty: not known yet.
    /// </summary>
    [JsonConverter(typeof(NullableHexValueConverter))]
    public uint? PcInput { get; set; }

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

/// <summary><see cref="HexValueConverter"/> for optional values; null stays null.</summary>
public sealed class NullableHexValueConverter : JsonConverter<uint?>
{
    private static readonly HexValueConverter Inner = new();

    public override bool HandleNull => true;

    public override uint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(uint), options);

    public override void Write(Utf8JsonWriter writer, uint? value, JsonSerializerOptions options)
    {
        if (value is uint v) Inner.Write(writer, v, options);
        else writer.WriteNullValue();
    }
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
