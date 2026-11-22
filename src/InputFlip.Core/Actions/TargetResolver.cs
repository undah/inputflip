using InputFlip.Core.Ddc;
using InputFlip.Core.Monitors;
using InputFlip.Core.Settings;

namespace InputFlip.Core.Actions;

/// <summary>
/// Finds the monitor and input a command means from what someone typed: a name ("MSI", "PS5"), a port ("HDMI 1",
/// "hdmi1"), a DDC/CI value ("0x11"), a monitor's number in the window ("2"), or "main".
/// </summary>
public static class TargetResolver
{
    /// <param name="monitors">Connected monitors in the window's order.</param>
    public static MonitorSnapshot? ResolveMonitor(string? spec, IReadOnlyList<MonitorSnapshot> monitors, AppSettings settings, out string? problem)
    {
        problem = null;
        var usable = monitors.Where(m => m.Health != MonitorHealth.Unavailable).ToList();
        if (usable.Count == 0)
        {
            problem = "InputFlip can't control any monitor right now. Check that DDC/CI is on in the monitor's menu.";
            return null;
        }

        string key = spec?.Trim() ?? "";
        if (key.Length == 0 || key.Equals("main", StringComparison.OrdinalIgnoreCase) || key.Equals("primary", StringComparison.OrdinalIgnoreCase))
            return usable.FirstOrDefault(m => m.IsPrimary) ?? usable[0];

        if (int.TryParse(key, out int number))
        {
            if (number >= 1 && number <= monitors.Count && monitors[number - 1].Health != MonitorHealth.Unavailable) return monitors[number - 1];
            problem = $"There's no monitor {number}. InputFlip numbers them 1 to {monitors.Count}, in the order its window shows them.";
            return null;
        }

        string Name(MonitorSnapshot m) =>
            settings.Monitors.TryGetValue(m.Id, out var ms) && !string.IsNullOrWhiteSpace(ms.Nickname) ? ms.Nickname!.Trim() : m.Name;

        var match = Pick(usable, key, m => [m.Id, Name(m), m.Name], out var several);
        if (match is not null) return match;
        if (several.Count > 1)
        {
            problem = $"\"{key}\" could be {JoinOr(several.Select(Name))}. Use more of the name, or the monitor's number.";
            return null;
        }
        problem = $"No connected monitor is called \"{key}\". Connected: {string.Join(", ", usable.Select(Name))}.";
        return null;
    }

    /// <summary>The input value for <paramref name="spec"/> on this monitor, or null with a problem to show.</summary>
    public static uint? ResolveInput(string? spec, MonitorSnapshot monitor, MonitorSettings? settings, out string? problem)
    {
        problem = null;
        string key = spec?.Trim() ?? "";
        var inputs = InputLayout.Build(monitor, settings);
        if (key.Length == 0)
        {
            problem = "Say which input, such as PS5 or HDMI 1.";
            return null;
        }
        if (key.Equals("pc", StringComparison.OrdinalIgnoreCase) || key.Equals("this pc", StringComparison.OrdinalIgnoreCase))
        {
            var pc = inputs.FirstOrDefault(i => i.IsThisPc);
            if (pc is not null) return pc.Value;
        }

        var match = Pick(inputs, key, i => [i.Name, i.PortName], out var several);
        if (match is not null) return match.Value;
        if (several.Count > 1)
        {
            problem = $"\"{key}\" could be {JoinOr(several.Select(i => i.Name))}. Use the full name or the port, such as HDMI 1.";
            return null;
        }
        // A value works even when the monitor doesn't list it.
        if (HexValueConverter.TryParse(key, out uint value) && value is > 0 and <= 0xFFFF) return value;

        problem = $"{monitor.Name} has no input called \"{key}\". It has {string.Join(", ", inputs.Where(i => !i.Hidden).Select(i => i.Name))}.";
        return null;
    }

    /// <summary>Exact match first (ignoring case), then ignoring spaces and dashes, then a unique partial match.</summary>
    private static T? Pick<T>(IReadOnlyList<T> items, string key, Func<T, string[]> names, out List<T> several) where T : class
    {
        several = [];
        var exact = items.Where(i => names(i).Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) { several = exact; return null; }

        string squashed = Squash(key);
        var loose = items.Where(i => names(i).Any(n => Squash(n) == squashed)).ToList();
        if (loose.Count == 1) return loose[0];
        if (loose.Count > 1) { several = loose; return null; }

        var partial = items.Where(i => names(i).Any(n => Squash(n).Contains(squashed, StringComparison.Ordinal))).ToList();
        if (partial.Count == 1) return partial[0];
        several = partial;
        return null;
    }

    private static string Squash(string text) =>
        new string(text.Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '_' and not '.').ToArray()).ToLowerInvariant();

    private static string JoinOr(IEnumerable<string> names)
    {
        var list = names.Distinct().ToList();
        return list.Count <= 1 ? string.Join("", list) : string.Join(", ", list.Take(list.Count - 1)) + " or " + list[^1];
    }
}
