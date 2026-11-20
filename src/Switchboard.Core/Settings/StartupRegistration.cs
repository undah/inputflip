using Microsoft.Win32;

namespace Switchboard.Core.Settings;

/// <summary>
/// "Start with Windows": a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Kept in the registry
/// rather than settings.json, because that's what Windows reads (and what Task Manager's Startup apps page changes).
/// </summary>
public sealed class StartupRegistration(string keyPath = StartupRegistration.RunKey, string valueName = "Switchboard")
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Task Manager's on/off switch for Run entries lives here; an odd first byte means "turned off".
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public bool IsEnabled(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is string command && SamePath(command, exePath) && !TurnedOffInTaskManager();
    }

    /// <summary>Whether any Switchboard entry exists, even for another copy of the exe.</summary>
    public bool Exists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is not null;
    }

    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName, Command(exePath));
        if (keyPath == RunKey)
        {
            // Turning it on here should win over an earlier "off" in Task Manager.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public static string Command(string exePath) => $"\"{exePath}\"";

    private static bool SamePath(string command, string exePath)
    {
        string trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            trimmed = end > 0 ? trimmed[1..end] : trimmed.Trim('"');
        }
        return string.Equals(trimmed, exePath, StringComparison.OrdinalIgnoreCase);
    }

    private bool TurnedOffInTaskManager()
    {
        if (keyPath != RunKey) return false;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(valueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }
}
