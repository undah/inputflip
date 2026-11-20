namespace Switchboard.Core;

/// <summary>
/// Plain-text log in %LOCALAPPDATA%\Switchboard. Raw error details go here, never into the UI.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath { get; set; } = Path.Combine(AppPaths.DataDirectory, "switchboard.log");

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
    /// <summary>%LOCALAPPDATA%\Switchboard: settings.json and switchboard.log.</summary>
    public static string DefaultDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchboard");

    public static string DataDirectory { get; private set; } = DefaultDataDirectory;

    /// <summary>True when started with --data-dir or SWITCHBOARD_DATA_DIR (testing, first-run setup).</summary>
    public static bool IsCustomDataDirectory { get; private set; }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Uses another folder for settings and the log. Call before anything else is logged.</summary>
    public static void UseDataDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        DataDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim()));
        IsCustomDataDirectory = !string.Equals(DataDirectory, DefaultDataDirectory, StringComparison.OrdinalIgnoreCase);
        Log.FilePath = Path.Combine(DataDirectory, "switchboard.log");
    }
}
