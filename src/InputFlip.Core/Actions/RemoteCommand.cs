using System.Globalization;
using System.Text.Json;

namespace InputFlip.Core.Actions;

public enum CommandVerb
{
    Switch,
    Scene,
    Toggle,
    Brightness,
    Contrast,
    Volume,
    Mute,
    Unmute,
    List,
    Picker,
    Show,
}

/// <summary>A level for brightness, contrast or volume: 0 to 100, or a step up or down from where it is ("+10").</summary>
public readonly record struct Level(int Amount, bool Relative)
{
    public static bool TryParse(string? text, out Level level)
    {
        level = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().TrimEnd('%');
        bool relative = text.StartsWith('+') || text.StartsWith('-');
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int amount)) return false;
        if (!relative && amount is < 0 or > 100) return false;
        if (relative && amount is < -100 or > 100) return false;
        level = new Level(amount, relative);
        return true;
    }

    /// <summary>The new percentage, given the current one.</summary>
    public int Apply(int current) => Math.Clamp(Relative ? current + Amount : Amount, 0, 100);

    public override string ToString() => Relative ? (Amount >= 0 ? $"+{Amount}" : $"{Amount}") : $"{Amount}";
}

/// <summary>
/// Something to do, from the command line (<c>InputFlip.exe --switch MSI PS5</c>) or a link
/// (<c>inputflip://switch/MSI/PS5</c>). Monitor and input are as typed; <see cref="TargetResolver"/> finds them.
/// </summary>
/// <param name="Monitor">A monitor's name, id or number, "main", or "all" (brightness, contrast, volume). Null: the main monitor.</param>
/// <param name="Target">The input (Switch) or the scene (Scene).</param>
public sealed record RemoteCommand(CommandVerb Verb, string? Monitor = null, string? Target = null, Level? Level = null)
{
    /// <summary>The scheme for links: <c>inputflip://...</c>.</summary>
    public const string UriScheme = "inputflip";

    private static readonly Dictionary<string, CommandVerb> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["switch"] = CommandVerb.Switch,
        ["scene"] = CommandVerb.Scene,
        ["toggle"] = CommandVerb.Toggle,
        ["brightness"] = CommandVerb.Brightness,
        ["contrast"] = CommandVerb.Contrast,
        ["volume"] = CommandVerb.Volume,
        ["mute"] = CommandVerb.Mute,
        ["unmute"] = CommandVerb.Unmute,
        ["list"] = CommandVerb.List,
        ["picker"] = CommandVerb.Picker,
        ["show"] = CommandVerb.Show,
    };

    /// <summary>
    /// Finds a command in the command line. Returns null when there's none (a normal start). Other options
    /// (--data-dir, --no-switch...) are skipped. A link passed as the only argument (how Windows opens
    /// <c>inputflip://</c> links) works too.
    /// </summary>
    public static RemoteCommand? FromArgs(IReadOnlyList<string> args, out string? problem)
    {
        problem = null;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg.StartsWith(UriScheme + ":", StringComparison.OrdinalIgnoreCase)) return FromUri(arg, out problem);
            if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
            if (!Verbs.TryGetValue(arg[2..], out var verb)) continue;

            var values = new List<string>();
            for (int j = i + 1; j < args.Count && !args[j].StartsWith("--", StringComparison.Ordinal); j++) values.Add(args[j]);
            return Build(verb, values, $"--{arg[2..].ToLowerInvariant()}", out problem);
        }
        return null;
    }

    /// <summary>Reads an <c>inputflip://verb/part/part</c> link. Parts are URL-encoded ("HDMI%201").</summary>
    public static RemoteCommand? FromUri(string uri, out string? problem)
    {
        problem = null;
        string rest = uri.Trim();
        int colon = rest.IndexOf(':');
        rest = rest[(colon + 1)..].TrimStart('/');
        int query = rest.IndexOfAny(['?', '#']);
        if (query >= 0) rest = rest[..query];
        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (parts.Count == 0 || !Verbs.TryGetValue(parts[0], out var verb))
        {
            problem = $"InputFlip doesn't know the link {uri}. Links look like inputflip://switch/MSI/PS5 or inputflip://scene/Console.";
            return null;
        }
        return Build(verb, parts.Skip(1).ToList(), $"{UriScheme}://{parts[0].ToLowerInvariant()}", out problem);
    }

    private static RemoteCommand? Build(CommandVerb verb, List<string> values, string usedAs, out string? problem)
    {
        problem = null;
        switch (verb)
        {
            case CommandVerb.Switch:
                if (values.Count == 0) { problem = $"{usedAs} needs an input, such as {usedAs} MSI PS5."; return null; }
                return values.Count == 1
                    ? new RemoteCommand(verb, Target: values[0])
                    : new RemoteCommand(verb, Monitor: values[0], Target: string.Join(' ', values.Skip(1)));
            case CommandVerb.Scene:
                if (values.Count == 0) { problem = $"{usedAs} needs a scene name."; return null; }
                return new RemoteCommand(verb, Target: string.Join(' ', values));
            case CommandVerb.Brightness or CommandVerb.Contrast or CommandVerb.Volume:
                if (values.Count == 0 || !Actions.Level.TryParse(values[^1], out var level))
                {
                    problem = $"{usedAs} needs a level from 0 to 100, or a step such as +10 or -10.";
                    return null;
                }
                return new RemoteCommand(verb, Monitor: values.Count > 1 ? string.Join(' ', values.Take(values.Count - 1)) : null, Level: level);
            case CommandVerb.Toggle or CommandVerb.Mute or CommandVerb.Unmute:
                return new RemoteCommand(verb, Monitor: values.Count > 0 ? string.Join(' ', values) : null);
            default:
                return new RemoteCommand(verb);
        }
    }

    /// <summary>The command as command-line arguments, to send to the copy that's already running.</summary>
    public IReadOnlyList<string> ToArgs()
    {
        var args = new List<string> { "--" + Verb.ToString().ToLowerInvariant() };
        if (Monitor is not null) args.Add(Monitor);
        if (Target is not null) args.Add(Target);
        if (Level is { } level) args.Add(level.ToString());
        return args;
    }
}

/// <summary>
/// What goes down the pipe between a second start of InputFlip (a command) and the copy that's already running:
/// one line of JSON each way.
/// </summary>
public static class CommandPipe
{
    public sealed record Reply(bool Ok, string Message);

    public static string EncodeRequest(IReadOnlyList<string> args) => JsonSerializer.Serialize(args);

    public static IReadOnlyList<string>? DecodeRequest(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonSerializer.Deserialize<List<string>>(line); }
        catch (JsonException) { return null; }
    }

    public static string EncodeReply(Reply reply) => JsonSerializer.Serialize(new { ok = reply.Ok, message = reply.Message });

    public static Reply? DecodeReply(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return new Reply(root.GetProperty("ok").GetBoolean(), root.GetProperty("message").GetString() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
