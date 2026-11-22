using InputFlip.Core.Actions;
using InputFlip.Core.Settings;

namespace InputFlip.Core.Usb;

/// <summary>
/// Turns USB plug and unplug notices into actions. Windows sends several notices for one device (one per interface,
/// sometimes a repeat), and a USB switch box moves several devices at once, so the same action within
/// <see cref="Quiet"/> runs once.
/// </summary>
public sealed class UsbRuleEngine
{
    /// <summary>How long a rule's action is held back after it ran, so a burst of notices runs it once.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, DateTime> _lastRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The actions to run for a device that plugged in (<paramref name="connected"/>) or left. Each distinct action
    /// once, in rule order. Rules that are off, or have nothing for this event, are skipped.
    /// </summary>
    public IReadOnlyList<AppAction> OnDeviceChange(IEnumerable<UsbRule> rules, string devicePath, bool connected, DateTime now)
    {
        var device = UsbDeviceId.TryParse(devicePath);
        if (device is null) return [];

        var actions = new List<AppAction>();
        foreach (var rule in rules)
        {
            if (rule.Disabled || UsbDeviceId.TryParse(rule.Device) is not { } wanted || !wanted.Matches(device)) continue;
            if (AppAction.Parse(connected ? rule.WhenConnected : rule.WhenDisconnected) is not { } action) continue;
            string key = action.ToString();
            if (actions.Any(a => a.ToString() == key)) continue;
            if (_lastRun.TryGetValue(key, out var last) && now - last < Quiet) continue;
            _lastRun[key] = now;
            actions.Add(action);
        }
        return actions;
    }
}
