using System.Text;
using InputFlip.Core.Ddc;
using InputFlip.Core.Monitors;
using InputFlip.Core.Settings;

namespace InputFlip.Core.Actions;

/// <summary>One thing a scene (or a "go back") does to one connected monitor.</summary>
public sealed record PlannedStep(string MonitorId, uint? Input, int? Brightness);

/// <summary>What running a scene will do, and which of its monitors aren't connected (they're skipped).</summary>
public sealed record ScenePlan(IReadOnlyList<PlannedStep> Steps, IReadOnlyList<string> MissingMonitors);

/// <summary>The inputs monitors showed before a scene or a rule switched them, so they can be put back.</summary>
public sealed record RestorePoint(string? SceneId, IReadOnlyDictionary<string, uint> Inputs);

/// <summary>Scenes: naming, planning, whether one is showing now, and going back.</summary>
public static class ScenePlanner
{
    public static string DisplayName(SceneSettings scene) =>
        string.IsNullOrWhiteSpace(scene.Name) ? "Scene" : scene.Name.Trim();

    /// <summary>A short id from the name ("Console night" → "console-night"), not used by another scene.</summary>
    public static string MakeId(string? name, IEnumerable<SceneSettings> existing)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? "").Trim().ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        string stem = sb.ToString().Trim('-');
        if (stem.Length == 0) stem = "scene";
        if (stem.Length > 32) stem = stem[..32].Trim('-');

        var taken = existing.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(stem)) return stem;
        for (int n = 2; ; n++)
            if (!taken.Contains($"{stem}-{n}")) return $"{stem}-{n}";
    }

    public static SceneSettings? Find(AppSettings settings, string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        string key = idOrName.Trim();
        return settings.Scenes.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase))
            ?? settings.Scenes.FirstOrDefault(s => string.Equals(s.Name?.Trim(), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The steps for connected monitors, in the scene's order. Steps that do nothing are left out.</summary>
    public static ScenePlan Plan(SceneSettings scene, IReadOnlyList<MonitorSnapshot> monitors)
    {
        var steps = new List<PlannedStep>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in scene.Steps)
        {
            if (step.Input is null && step.Brightness is null) continue;
            if (!seen.Add(step.Monitor)) continue; // one step per monitor; the first wins
            var monitor = monitors.FirstOrDefault(m => string.Equals(m.Id, step.Monitor, StringComparison.OrdinalIgnoreCase));
            if (monitor is null || monitor.Health == MonitorHealth.Unavailable)
            {
                missing.Add(step.Monitor);
                continue;
            }
            int? brightness = step.Brightness is int b ? Math.Clamp(b, 0, 100) : null;
            steps.Add(new PlannedStep(monitor.Id, step.Input, brightness));
        }
        return new ScenePlan(steps, missing);
    }

    /// <summary>
    /// The scene is showing now: it switches at least one connected monitor, and every connected monitor it switches is
    /// on that input. Brightness isn't checked (it isn't read back all the time).
    /// </summary>
    public static bool IsActive(SceneSettings scene, IReadOnlyList<MonitorSnapshot> monitors)
    {
        var plan = Plan(scene, monitors);
        var switches = plan.Steps.Where(s => s.Input is not null).ToList();
        if (switches.Count == 0) return false;
        return switches.All(s =>
            monitors.First(m => string.Equals(m.Id, s.MonitorId, StringComparison.OrdinalIgnoreCase)).CurrentInput is uint current
            && InputCatalog.Matches(current, s.Input!.Value));
    }

    /// <summary>What the monitors a plan switches show now, to put back later. Monitors with an unknown input are left out.</summary>
    public static RestorePoint Remember(string? sceneId, IEnumerable<PlannedStep> steps, IReadOnlyList<MonitorSnapshot> monitors)
    {
        var inputs = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (step.Input is not uint target) continue;
            var monitor = monitors.FirstOrDefault(m => string.Equals(m.Id, step.MonitorId, StringComparison.OrdinalIgnoreCase));
            if (monitor?.CurrentInput is uint current && !InputCatalog.Matches(current, target)) inputs[monitor.Id] = current;
        }
        return new RestorePoint(sceneId, inputs);
    }

    /// <summary>Steps that put the remembered inputs back, for monitors that are connected and show something else now.</summary>
    public static IReadOnlyList<PlannedStep> PlanBack(RestorePoint point, IReadOnlyList<MonitorSnapshot> monitors)
    {
        var steps = new List<PlannedStep>();
        foreach (var (id, input) in point.Inputs)
        {
            var monitor = monitors.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (monitor is null || monitor.Health == MonitorHealth.Unavailable) continue;
            if (monitor.CurrentInput is uint current && InputCatalog.Matches(current, input)) continue;
            steps.Add(new PlannedStep(monitor.Id, input, null));
        }
        return steps;
    }

    /// <summary>A scene that switches every listed monitor to what it shows now: the starting point for "Save as a scene".</summary>
    public static SceneSettings FromCurrent(string name, IReadOnlyList<MonitorSnapshot> monitors, AppSettings settings)
    {
        var scene = new SceneSettings { Id = MakeId(name, settings.Scenes), Name = name.Trim() };
        foreach (var m in SettingsEditor.OrderMonitors(monitors, settings))
        {
            if (settings.Monitors.TryGetValue(m.Id, out var ms) && ms.Hidden) continue;
            if (m.CurrentInput is uint current) scene.Steps.Add(new SceneStep { Monitor = m.Id, Input = current });
        }
        return scene;
    }
}
