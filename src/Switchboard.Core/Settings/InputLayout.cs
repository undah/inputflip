using Switchboard.Core.Ddc;
using Switchboard.Core.Monitors;

namespace Switchboard.Core.Settings;

/// <summary>One input as the app shows it.</summary>
public sealed record DisplayInput(
    uint Value,
    string Name,
    string PortName,
    string Icon,
    bool Hidden,
    string? Hotkey,
    bool IsCurrent,
    bool BlankScreensWhenLeaving = false);

/// <summary>Combines what a monitor reports with what's in settings into the list of inputs to show.</summary>
public static class InputLayout
{
    public static readonly IReadOnlySet<string> Icons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pc", "laptop", "console", "tv", "display",
    };

    /// <summary>All inputs for a monitor in display order, hidden ones included (the caller filters).</summary>
    public static IReadOnlyList<DisplayInput> Build(MonitorSnapshot monitor, MonitorSettings? settings)
    {
        var values = new List<uint>();
        var byValue = new Dictionary<uint, InputSettings>();

        if (settings is not null)
        {
            foreach (var input in settings.Inputs)
            {
                if (byValue.TryAdd(input.Value, input)) values.Add(input.Value);
            }
        }

        if (monitor.AdvertisedInputs is not null)
        {
            foreach (uint v in monitor.AdvertisedInputs)
                if (!values.Contains(v)) values.Add(v);
        }

        if (monitor.CurrentInput is uint current && !values.Any(v => InputCatalog.Matches(current, v)))
            values.Add(current);

        // The capabilities string failed and we've never seen the monitor's input list: offer the usual ports
        // so the user can still switch. Not saved unless the user renames or hides one.
        bool neverListed = settings is null || settings.Inputs.Count <= 1;
        if (monitor.AdvertisedInputs is null && monitor.CapabilitiesFailed && neverListed && monitor.Health == MonitorHealth.Responding)
        {
            foreach (uint v in InputCatalog.FallbackInputs)
                if (!values.Contains(v)) values.Add(v);
        }

        return values.Select(v =>
        {
            byValue.TryGetValue(v, out var s);
            string port = InputCatalog.GetPortName(v);
            string? icon = s?.Icon;
            return new DisplayInput(
                v,
                string.IsNullOrWhiteSpace(s?.Name) ? port : s!.Name!.Trim(),
                port,
                icon is not null && Icons.Contains(icon) ? icon.ToLowerInvariant() : "display",
                s?.Hidden ?? false,
                string.IsNullOrWhiteSpace(s?.Hotkey) ? null : s!.Hotkey,
                monitor.CurrentInput is uint c && InputCatalog.Matches(c, v),
                settings?.BlankScreensWhenLeaving == v);
        }).ToList();
    }

    /// <summary>The input after the current one, wrapping around. With no current input, the first one.</summary>
    public static DisplayInput? Next(IReadOnlyList<DisplayInput> visible)
    {
        if (visible.Count == 0) return null;
        int index = -1;
        for (int i = 0; i < visible.Count; i++)
            if (visible[i].IsCurrent) { index = i; break; }
        return visible[(index + 1) % visible.Count];
    }

    /// <summary>
    /// Whether switching this monitor to <paramref name="target"/> needs the screens turned off first:
    /// the monitor has a "blank when leaving" input, and the target is a different input.
    /// </summary>
    public static bool NeedsScreensOff(MonitorSettings? settings, uint target) =>
        settings?.BlankScreensWhenLeaving is uint pcInput && !InputCatalog.Matches(target, pcInput) && target != pcInput;

    /// <summary>The settings entry for an input, created if needed.</summary>
    public static InputSettings GetOrAdd(MonitorSettings settings, uint value)
    {
        var existing = settings.Inputs.FirstOrDefault(i => i.Value == value);
        if (existing is not null) return existing;
        var created = new InputSettings { Value = value };
        settings.Inputs.Add(created);
        return created;
    }
}
