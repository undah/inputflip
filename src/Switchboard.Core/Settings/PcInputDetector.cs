using Switchboard.Core.Ddc;
using Switchboard.Core.Interop;
using Switchboard.Core.Monitors;

namespace Switchboard.Core.Settings;

/// <summary>
/// Works out which of a monitor's inputs this PC is plugged into. DDC/CI can't say which inputs have a signal
/// (docs/ddc-ci-notes.md), but Windows reports the cable type, and only some of the monitor's ports fit it.
/// </summary>
public static class PcInputDetector
{
    /// <summary>
    /// The PC's input as far as settings know: the one set or remembered, else the screens-off input
    /// (which is the PC's input by definition). Null when neither is set.
    /// </summary>
    public static uint? FromSettings(MonitorSettings? settings) => settings?.PcInput ?? settings?.BlankScreensWhenLeaving;

    /// <summary>
    /// Picks the PC's input from the cable type: the one visible input whose port fits it, or, when several fit
    /// (two HDMI ports), the one the monitor shows now. Null when the monitor's input list isn't known yet or
    /// nothing settles it.
    /// </summary>
    /// <param name="inputs">The monitor's inputs as <see cref="InputLayout.Build"/> lists them.</param>
    public static uint? Detect(MonitorSnapshot monitor, MonitorSettings? settings, IReadOnlyList<uint> inputs)
    {
        // Before the input list is known, the only input listed may be the current one, which could be another device's.
        bool listKnown = monitor.AdvertisedInputs is not null || monitor.CapabilitiesFailed || (settings?.Inputs.Count ?? 0) > 1;
        if (!listKnown) return null;

        var candidates = inputs.Where(v => Fits(monitor.Connection, v) && !IsSomethingElse(settings, v)).ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1 && monitor.Health == MonitorHealth.Responding && monitor.CurrentInput is uint current)
        {
            var showing = candidates.Where(v => InputCatalog.Matches(current, v)).ToList();
            if (showing.Count == 1) return showing[0];
        }
        return null;
    }

    /// <summary>
    /// Remembers the PC's input for monitors that don't have one yet, so it doesn't move when the monitor later
    /// shows another device. Returns true if anything changed and the settings should be saved.
    /// </summary>
    public static bool Learn(AppSettings settings, IEnumerable<MonitorSnapshot> monitors)
    {
        bool changed = false;
        foreach (var m in monitors)
        {
            if (!settings.Monitors.TryGetValue(m.Id, out var ms) || FromSettings(ms) is not null) continue;
            var values = InputLayout.Build(m, ms).Select(i => i.Value).ToList();
            if (Detect(m, ms, values) is uint found)
            {
                ms.PcInput = found;
                changed = true;
                Log.Info($"{m.Name}: this PC looks to be on {InputCatalog.FormatValue(found)} ({m.Connection} cable)");
            }
        }
        return changed;
    }

    /// <summary>
    /// Whether a monitor port can carry what this PC's cable sends. HDMI and DVI share a signal (DVI-to-HDMI
    /// cables are common: Windows reports one of the owner's Acers as DVI), and USB-C carries DisplayPort.
    /// </summary>
    public static bool Fits(ConnectionKind connection, uint value) => (connection, value & 0xFF) switch
    {
        (ConnectionKind.DisplayPort, 0x0F or 0x10 or 0x1B) => true,
        (ConnectionKind.Hdmi or ConnectionKind.Dvi, 0x03 or 0x04 or 0x11 or 0x12) => true,
        (ConnectionKind.Vga, 0x01 or 0x02) => true,
        _ => false,
    };

    /// <summary>Hidden, or marked by the user as a console or TV.</summary>
    private static bool IsSomethingElse(MonitorSettings? settings, uint value)
    {
        var s = settings?.Inputs.FirstOrDefault(i => i.Value == value);
        return s is not null && (s.Hidden || s.Icon is "console" or "tv");
    }
}
