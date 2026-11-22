namespace InputFlip.Core;

/// <summary>
/// Plain-text log (inputflip.log) next to settings.json. Raw error details go here, never into the UI.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath { get; set; } = Path.Combine(AppPaths.DataDirectory, AppPaths.LogFileName);

    public static void Info(string message) => Write("info", message);

    public static void Warn(string message) => Write("warn", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("error", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

public static class AppPaths
{
    /// <summary>The folder for new installs.</summary>
    public const string FolderName = "InputFlip";

    /// <summary>The folder from before the rename (versions 1.0.x, "Switchboard"). Kept when it has settings in it.</summary>
    public const string LegacyFolderName = "Switchboard";

    public const string LogFileName = "inputflip.log";

    /// <summary>%LOCALAPPDATA%\InputFlip, or %LOCALAPPDATA%\Switchboard for a PC that used InputFlip before the rename.</summary>
    public static string DefaultDataDirectory { get; } =
        PickDefault(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), File.Exists);

    public static string DataDirectory { get; private set; } = DefaultDataDirectory;

    /// <summary>True when started with --data-dir or INPUTFLIP_DATA_DIR (testing, first-run setup).</summary>
    public static bool IsCustomDataDirectory { get; private set; }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>
    /// The new folder if it has settings; else the old "Switchboard" folder if that has settings (so an update keeps
    /// them); else the new folder.
    /// </summary>
    public static string PickDefault(string localAppData, Func<string, bool> fileExists)
    {
        string current = Path.Combine(localAppData, FolderName);
        string legacy = Path.Combine(localAppData, LegacyFolderName);
        if (fileExists(Path.Combine(current, "settings.json"))) return current;
        if (fileExists(Path.Combine(legacy, "settings.json"))) return legacy;
        return current;
    }

    /// <summary>Uses another folder for settings and the log. Call before anything else is logged.</summary>
    public static void UseDataDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        DataDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim()));
        IsCustomDataDirectory = !string.Equals(DataDirectory, DefaultDataDirectory, StringComparison.OrdinalIgnoreCase);
        Log.FilePath = Path.Combine(DataDirectory, LogFileName);
    }
}
