using Switchboard.Core.Ddc;
using Switchboard.Core.Settings;

namespace Switchboard.Core.Actions;

public enum AppActionKind
{
    /// <summary>Run a scene.</summary>
    Scene,

    /// <summary>Switch one monitor to one input.</summary>
    Switch,

    /// <summary>Put back the inputs from before the last rule's switch (USB rules, lock and sleep).</summary>
    Back,
}

/// <summary>
/// What a USB rule or a lock/sleep rule does, stored as text in settings.json so it stays easy to edit by hand:
/// <c>scene:console</c>, <c>switch:MSI3CB0-000000D1:0x11</c> or <c>back</c>.
/// </summary>
public sealed record AppAction(AppActionKind Kind, string? SceneId = null, string? MonitorId = null, uint Input = 0)
{
    public static AppAction ForScene(string sceneId) => new(AppActionKind.Scene, SceneId: sceneId);

    public static AppAction ForSwitch(string monitorId, uint input) => new(AppActionKind.Switch, MonitorId: monitorId, Input: input);

    public static AppAction Back { get; } = new(AppActionKind.Back);

    /// <summary>Reads the text form. Empty or unreadable text is no action (null).</summary>
    public static AppAction? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Equals("back", StringComparison.OrdinalIgnoreCase)) return Back;
        if (text.StartsWith("scene:", StringComparison.OrdinalIgnoreCase))
        {
            string id = text[6..].Trim();
            return id.Length == 0 ? null : ForScene(id);
        }
        if (text.StartsWith("switch:", StringComparison.OrdinalIgnoreCase))
        {
            // Monitor ids contain no colon, but read the input from the last one to be safe.
            int last = text.LastIndexOf(':');
            if (last <= 7) return null;
            string monitor = text[7..last].Trim();
            if (monitor.Length == 0 || !HexValueConverter.TryParse(text[(last + 1)..], out uint input) || input == 0) return null;
            return ForSwitch(monitor, input);
        }
        return null;
    }

    public override string ToString() => Kind switch
    {
        AppActionKind.Scene => $"scene:{SceneId}",
        AppActionKind.Switch => $"switch:{MonitorId}:{InputCatalog.FormatValue(Input)}",
        _ => "back",
    };

    /// <summary>"Run Console", "MSI G27CQ4 to PS5", "Go back", for the settings window and the log.</summary>
    public string Describe(AppSettings settings, Func<string, string> monitorName)
    {
        switch (Kind)
        {
            case AppActionKind.Scene:
                var scene = settings.Scenes.FirstOrDefault(s => string.Equals(s.Id, SceneId, StringComparison.OrdinalIgnoreCase));
                return scene is null ? $"Run a scene that's been deleted ({SceneId})" : $"Run {ScenePlanner.DisplayName(scene)}";
            case AppActionKind.Switch:
                string input = InputCatalog.GetPortName(Input);
                if (settings.Monitors.TryGetValue(MonitorId!, out var ms)
                    && ms.Inputs.FirstOrDefault(i => i.Value == Input)?.Name is { } name && !string.IsNullOrWhiteSpace(name))
                    input = name.Trim();
                return $"{monitorName(MonitorId!)} to {input}";
            default:
                return "Go back to what was showing before";
        }
    }
}
