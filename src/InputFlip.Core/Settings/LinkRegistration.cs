using Microsoft.Win32;
using InputFlip.Core.Actions;

namespace InputFlip.Core.Settings;

/// <summary>
/// Makes <c>inputflip://</c> links open InputFlip (for Stream Deck's "Website" action, desktop shortcuts, a browser
/// bookmark). Per user, under HKCU\Software\Classes, so it needs no admin rights; removed on uninstall.
/// </summary>
public sealed class LinkRegistration(string classesPath = @"Software\Classes")
{
    private string KeyPath => $@"{classesPath}\{RemoteCommand.UriScheme}";

    /// <summary>The command Windows runs for a link: the exe with the link as its argument.</summary>
    public static string Command(string exePath) => $"\"{exePath}\" \"%1\"";

    public bool IsRegistered(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{KeyPath}\shell\open\command");
        return key?.GetValue(null) is string command && string.Equals(command, Command(exePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Points links at this exe (after an update or a move, the path changes).</summary>
    public void Register(string exePath)
    {
        using var root = Registry.CurrentUser.CreateSubKey(KeyPath);
        root.SetValue(null, "URL:InputFlip link");
        root.SetValue("URL Protocol", "");
        using (var icon = root.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exePath}\",0");
        using var command = root.CreateSubKey(@"shell\open\command");
        command.SetValue(null, Command(exePath));
    }

    public void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
}
