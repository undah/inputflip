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
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchboard");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}
