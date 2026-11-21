using Switchboard.Core.Monitors;
using Switchboard.Core.Settings;

namespace Switchboard.Core.Actions;

/// <summary>One choice in the pop-up picker: a scene, or an input of a monitor.</summary>
/// <param name="Group">The heading it sits under: "Scenes" or the monitor's name.</param>
public sealed record PickerItem(string Group, string Label, string Icon, bool IsCurrent, AppAction Action, string? Detail = null);

/// <summary>What the pop-up picker lists: scenes first, then every shown monitor's shown inputs, in the window's order.</summary>
public static class PickerItems
{
    public static IReadOnlyList<PickerItem> Build(IReadOnlyList<MonitorSnapshot> monitors, AppSettings settings)
    {
        var items = new List<PickerItem>();
        foreach (var scene in settings.Scenes)
        {
            if (string.IsNullOrWhiteSpace(scene.Id)) continue;
            string icon = scene.Icon is not null && InputLayout.Icons.Contains(scene.Icon) ? scene.Icon.ToLowerInvariant() : "scene";
            items.Add(new PickerItem("Scenes", ScenePlanner.DisplayName(scene), icon, ScenePlanner.IsActive(scene, monitors),
                AppAction.ForScene(scene.Id), scene.Hotkey));
        }

        foreach (var m in SettingsEditor.OrderMonitors(monitors, settings))
        {
            if (m.Health == MonitorHealth.Unavailable) continue;
            settings.Monitors.TryGetValue(m.Id, out var ms);
            if (ms?.Hidden == true) continue;
            string group = string.IsNullOrWhiteSpace(ms?.Nickname) ? m.Name : ms!.Nickname!.Trim();
            foreach (var input in InputLayout.Build(m, ms).Where(i => !i.Hidden))
                items.Add(new PickerItem(group, input.Name, input.Icon, input.IsCurrent, AppAction.ForSwitch(m.Id, input.Value),
                    input.Name == input.PortName ? input.Hotkey : input.PortName));
        }
        return items;
    }

    /// <summary>
    /// Where the selection starts: the first scene or input that isn't showing now (the likely next one), else the first item.
    /// </summary>
    public static int InitialSelection(IReadOnlyList<PickerItem> items)
    {
        for (int i = 0; i < items.Count; i++)
            if (!items[i].IsCurrent) return i;
        return 0;
    }
}
