using Switchboard.Core.Ddc;
using Switchboard.Core.Monitors;

namespace Switchboard.Core.Settings;

/// <summary>The changes the settings window makes, kept here so they're unit-tested.</summary>
public static class SettingsEditor
{
    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";

    /// <summary>Connected monitors in the order settings.json lists them; new ones keep Windows' order, after the rest.</summary>
    public static IReadOnlyList<MonitorSnapshot> OrderMonitors(IEnumerable<MonitorSnapshot> monitors, AppSettings settings)
    {
        var order = settings.Monitors.Keys.Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index, StringComparer.OrdinalIgnoreCase);
        return monitors
            .Select((m, index) => (m, index))
            .OrderBy(x => order.TryGetValue(x.m.Id, out int at) ? at : int.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.m)
            .ToList();
    }

    /// <summary>Moves a monitor up (-1) or down (+1) in settings.json, which sets the order the app shows them in.</summary>
    public static bool MoveMonitor(AppSettings settings, string id, int delta)
    {
        var ids = settings.Monitors.Keys.ToList();
        int from = ids.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        int to = from + delta;
        if (from < 0 || to < 0 || to >= ids.Count) return false;
        (ids[from], ids[to]) = (ids[to], ids[from]);

        var reordered = new Dictionary<string, MonitorSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in ids) reordered[key] = settings.Monitors[key];
        settings.Monitors = reordered;
        return true;
    }

    /// <summary>
    /// Moves an input up (-1) or down (+1). <paramref name="shown"/> is the full list as the app shows it,
    /// which can include inputs not saved yet; they're saved in that order first so the move does what you see.
    /// </summary>
    public static bool MoveInput(MonitorSettings settings, IReadOnlyList<uint> shown, uint value, int delta)
    {
        var order = shown.ToList();
        foreach (var saved in settings.Inputs)
            if (!order.Contains(saved.Value)) order.Add(saved.Value);

        int from = order.IndexOf(value);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= order.Count) return false;
        (order[from], order[to]) = (order[to], order[from]);

        var byValue = settings.Inputs.GroupBy(i => i.Value).ToDictionary(g => g.Key, g => g.First());
        settings.Inputs = order.Select(v => byValue.TryGetValue(v, out var s) ? s : new InputSettings { Value = v }).ToList();
        return true;
    }

    /// <summary>
    /// Adds an input by its DDC/CI value ("0x11", "17", "11h"). Returns an error to show, or null.
    /// An input that's already listed but hidden is shown again.
    /// </summary>
    public static string? AddInput(MonitorSettings settings, IEnumerable<uint> shown, string? text, out uint value)
    {
        value = 0;
        if (!HexValueConverter.TryParse(text, out value) || value == 0 || value > 0xFFFF)
            return "Enter the input's value, such as 0x11 for HDMI 1.";

        uint v = value;
        var existing = settings.Inputs.FirstOrDefault(i => i.Value == v);
        if (existing is { Hidden: true })
        {
            existing.Hidden = false;
            return null;
        }
        if (existing is not null || shown.Contains(v))
            return $"{InputCatalog.GetPortName(v)} ({InputCatalog.FormatValue(v)}) is already in the list.";

        settings.Inputs.Add(new InputSettings { Value = v, Name = null });
        return null;
    }

    /// <summary>Whether an input can be removed: only ones the monitor doesn't list itself (they'd come straight back).</summary>
    public static bool CanRemoveInput(MonitorSnapshot? monitor, uint value) =>
        monitor?.AdvertisedInputs is not { } advertised || !advertised.Contains(value);

    public static bool RemoveInput(MonitorSettings settings, uint value)
    {
        bool removed = settings.Inputs.RemoveAll(i => i.Value == value) > 0;
        if (settings.BlankScreensWhenLeaving == value) settings.BlankScreensWhenLeaving = null;
        if (settings.PcInput == value) settings.PcInput = null;
        return removed;
    }

    /// <summary>"light", "dark" or null (follow Windows) from whatever is in the file.</summary>
    public static string? NormalizeTheme(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        ThemeLight => ThemeLight,
        ThemeDark => ThemeDark,
        _ => null,
    };
}
