using Switchboard.Core.Ddc;
using Switchboard.Core.Settings;

namespace Switchboard.Core.Hotkeys;

/// <summary>
/// Every place in settings that holds a shortcut, under one key each: "show", "cycle",
/// or "input:&lt;monitor id&gt;:0x11". Used by the shortcut recorder to read, write and find conflicts.
/// </summary>
public static class ShortcutSlots
{
    public const string Show = "show";
    public const string Cycle = "cycle";

    public static string InputKey(string monitorId, uint value) => $"input:{monitorId}:{InputCatalog.FormatValue(value)}";

    public static bool TryParseInputKey(string key, out string monitorId, out uint value)
    {
        monitorId = "";
        value = 0;
        if (!key.StartsWith("input:", StringComparison.Ordinal)) return false;
        int last = key.LastIndexOf(':');
        if (last <= 6) return false;
        monitorId = key[6..last];
        return HexValueConverter.TryParse(key[(last + 1)..], out value);
    }

    /// <summary>All slots that have a shortcut, in settings order.</summary>
    public static IEnumerable<(string Key, string Text)> All(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ShowHotkey)) yield return (Show, settings.ShowHotkey!);
        if (!string.IsNullOrWhiteSpace(settings.CycleHotkey)) yield return (Cycle, settings.CycleHotkey!);
        foreach (var (monitorId, ms) in settings.Monitors)
            foreach (var input in ms.Inputs)
                if (!string.IsNullOrWhiteSpace(input.Hotkey)) yield return (InputKey(monitorId, input.Value), input.Hotkey!);
    }

    public static string? Get(AppSettings settings, string key)
    {
        if (key == Show) return settings.ShowHotkey;
        if (key == Cycle) return settings.CycleHotkey;
        if (TryParseInputKey(key, out string id, out uint value) && settings.Monitors.TryGetValue(id, out var ms))
            return ms.Inputs.FirstOrDefault(i => i.Value == value)?.Hotkey;
        return null;
    }

    /// <summary>Sets or clears (null) a shortcut. Returns false for an input of a monitor settings don't know.</summary>
    public static bool Set(AppSettings settings, string key, string? text)
    {
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (key == Show) settings.ShowHotkey = text;
        else if (key == Cycle) settings.CycleHotkey = text;
        else if (TryParseInputKey(key, out string id, out uint value) && settings.Monitors.TryGetValue(id, out var ms))
            InputLayout.GetOrAdd(ms, value).Hotkey = text;
        else return false;
        return true;
    }

    /// <summary>The other slot already using <paramref name="hotkey"/>, or null.</summary>
    public static string? FindOwner(AppSettings settings, Hotkey hotkey, string exceptKey)
    {
        foreach (var (key, text) in All(settings))
        {
            if (key == exceptKey) continue;
            if (Hotkey.TryParse(text, out var other, out _) && other == hotkey) return key;
        }
        return null;
    }

    /// <summary>Slots whose shortcut is also used by an earlier slot (only the first one gets registered).</summary>
    public static IReadOnlyDictionary<string, string> Duplicates(AppSettings settings)
    {
        var first = new Dictionary<Hotkey, string>();
        var duplicates = new Dictionary<string, string>();
        foreach (var (key, text) in All(settings))
        {
            if (!Hotkey.TryParse(text, out var hotkey, out _)) continue;
            if (first.TryGetValue(hotkey, out string? owner)) duplicates[key] = owner;
            else first[hotkey] = key;
        }
        return duplicates;
    }
}
