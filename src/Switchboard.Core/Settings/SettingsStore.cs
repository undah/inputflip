using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Switchboard.Core.Ddc;
using Switchboard.Core.Monitors;

namespace Switchboard.Core.Settings;

public sealed record SettingsLoadResult(AppSettings Settings, bool FileExisted, string? Problem);

/// <summary>Loads and saves settings.json. Never overwrites a file it couldn't read, so hand edits aren't lost.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Leave out nulls and false flags so the file stays short enough to edit by hand.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        PropertyNameCaseInsensitive = true,
        // Write "Ctrl+Alt+Shift+I", not "Ctrl+Alt...". The file is local, never served to a browser.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SettingsStore(string path)
    {
        FilePath = path;
    }

    public string FilePath { get; }

    /// <summary>True after a load failed; saving is blocked until the file loads cleanly again.</summary>
    public bool IsBroken { get; private set; }

    public DateTime LastSavedUtc { get; private set; }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath))
        {
            IsBroken = false;
            return new SettingsLoadResult(new AppSettings(), false, null);
        }

        try
        {
            string json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            settings.Monitors = new Dictionary<string, MonitorSettings>(
                settings.Monitors ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach (var monitor in settings.Monitors.Values)
                monitor.Inputs ??= new();
            IsBroken = false;
            return new SettingsLoadResult(settings, true, null);
        }
        catch (JsonException ex)
        {
            IsBroken = true;
            Log.Warn($"settings.json didn't load: {ex.Message}");
            string where = ex.LineNumber is long line ? $" on line {line + 1}" : "";
            return new SettingsLoadResult(
                new AppSettings(),
                true,
                $"Your settings file has a mistake{where}, so Switchboard is using defaults. Fix it and save; it reloads by itself.");
        }
        catch (IOException ex)
        {
            IsBroken = true;
            Log.Warn($"settings.json couldn't be read: {ex.Message}");
            return new SettingsLoadResult(new AppSettings(), true, "Switchboard couldn't open its settings file, so it's using defaults.");
        }
    }

    public bool Save(AppSettings settings)
    {
        if (IsBroken)
        {
            Log.Info("Not saving settings: the file on disk didn't load, and saving would overwrite it.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, FilePath, overwrite: true);
            LastSavedUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving settings failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Adds settings for monitors seen for the first time, and inputs the monitor reports that aren't listed yet.
    /// Returns true if anything changed and the settings should be saved.
    /// </summary>
    public static bool MergeDetected(AppSettings settings, IEnumerable<MonitorSnapshot> monitors)
    {
        bool changed = false;
        foreach (var m in monitors)
        {
            if (!settings.Monitors.TryGetValue(m.Id, out var ms))
            {
                ms = new MonitorSettings { Nickname = m.Name };
                settings.Monitors[m.Id] = ms;
                changed = true;
            }

            var known = ms.Inputs.Select(i => i.Value).ToHashSet();
            var detected = new List<uint>();
            if (m.AdvertisedInputs is not null) detected.AddRange(m.AdvertisedInputs);
            if (m.CurrentInput is uint current && !detected.Any(v => InputCatalog.Matches(current, v)))
                detected.Add(current);

            foreach (uint value in detected)
            {
                if (known.Add(value))
                {
                    ms.Inputs.Add(new InputSettings { Value = value });
                    changed = true;
                }
            }
        }
        return changed;
    }
}
