using Microsoft.Win32;

namespace InputFlip.Core.Settings;

/// <summary>
/// "Start with Windows": a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Kept in the registry
/// rather than settings.json, because that's what Windows reads (and what Task Manager's Startup apps page changes).
/// </summary>
public sealed class StartupRegistration(string keyPath = StartupRegistration.RunKey)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ValueName = "InputFlip";

    /// <summary>The value's name before the rename (versions 1.0.x); see <see cref="MigrateLegacy"/>.</summary>
    public const string LegacyValueName = "Switchboard";

    // Task Manager's on/off switch for Run entries lives here; an odd first byte means "turned off".
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public bool IsEnabled(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ValueName) is string command && SamePath(command, exePath) && !TurnedOffInTaskManager(ValueName);
    }

    /// <summary>Whether any InputFlip entry exists (also the old Switchboard one), even for another copy of the exe.</summary>
    public bool Exists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ValueName) is not null || key?.GetValue(LegacyValueName) is not null;
    }

    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(ValueName, Command(exePath));
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (keyPath == RunKey)
        {
            // Turning it on here should win over an earlier "off" in Task Manager.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Before the rename the entry was called "Switchboard" and pointed at Switchboard.exe, which an update to InputFlip
    /// removes. Moves it to "InputFlip" for this exe, unless Task Manager had it turned off. True if it moved.
    /// </summary>
    public bool MigrateLegacy(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key?.GetValue(LegacyValueName) is null) return false;
        bool wasOn = !TurnedOffInTaskManager(LegacyValueName);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (wasOn) Enable(exePath);
        return true;
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

    private bool TurnedOffInTaskManager(string valueName)
    {
        if (keyPath != RunKey) return false;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(valueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }
}
