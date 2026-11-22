namespace Switchboard.Core;

/// <summary>
/// Command-line options. Normal use needs none; they're for testing and diagnostics:
/// <list type="bullet">
/// <item><c>--data-dir &lt;folder&gt;</c> (or <c>INPUTFLIP_DATA_DIR</c>): use another folder for settings.json and the log.
/// A folder without settings.json starts the first-run setup.</item>
/// <item><c>--no-switch</c>: never send a switch command. Reads still work.</item>
/// <item><c>--open settings[:general|monitors|shortcuts|about]</c> or <c>--open setup</c>: open that window at start.</item>
/// <item><c>--export-icon &lt;file.ico&gt;</c>: write the app icon and quit (used to make src/Switchboard/Assets/Switchboard.ico).</item>
/// </list>
/// </summary>
public sealed record StartupOptions(string? DataDirectory, bool NoSwitch, string? Open, string? ExportIconPath, IReadOnlyList<string> Problems)
{
    public static StartupOptions Parse(IReadOnlyList<string> args, string? dataDirectoryVariable = null)
    {
        string? dataDir = string.IsNullOrWhiteSpace(dataDirectoryVariable) ? null : dataDirectoryVariable;
        bool noSwitch = false;
        string? open = null, exportIcon = null;
        var problems = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? Value()
            {
                // Accept both "--data-dir x" and "--data-dir=x".
                int eq = arg.IndexOf('=');
                if (eq > 0) return arg[(eq + 1)..];
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[++i];
                problems.Add($"{arg} needs a value.");
                return null;
            }

            string name = arg.Split('=')[0].ToLowerInvariant();
            switch (name)
            {
                case "--data-dir": dataDir = Value() ?? dataDir; break;
                case "--no-switch": noSwitch = true; break;
                case "--open": open = Value()?.Trim().ToLowerInvariant(); break;
                case "--export-icon": exportIcon = Value(); break;
                // Commands (--switch MSI PS5 and the like) are read by RemoteCommand, along with their values.
                case "--switch" or "--scene" or "--toggle" or "--brightness" or "--contrast" or "--volume" or "--mute" or "--unmute" or "--list" or "--picker" or "--show":
                    break;
                default:
                    // Velopack and Windows can pass their own arguments; ignore what isn't ours.
                    if (arg.StartsWith("--", StringComparison.Ordinal)) problems.Add($"Unknown option {arg}.");
                    break;
            }
        }
        return new StartupOptions(dataDir, noSwitch, open, exportIcon, problems);
    }
}
