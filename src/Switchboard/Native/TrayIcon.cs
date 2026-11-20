using System.Runtime.InteropServices;
using Microsoft.Win32;
using Switchboard.Core;

namespace Switchboard.Native;

/// <summary>The notification-area icon, drawn in code so it matches the taskbar's light or dark theme.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private readonly HostWindow _host;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(HostWindow host)
    {
        _host = host;
        _host.TaskbarCreated += () => { _added = false; Show(); };
        _host.ThemeChanged += Refresh;
    }

    public string Tooltip { get; set; } = "Switchboard";

    public void Show()
    {
        ReplaceIcon();
        var data = MakeData();
        if (_added)
        {
            Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
            return;
        }
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);
        if (!_added)
        {
            Log.Warn("Adding the tray icon failed");
            return;
        }
        data.uVersion = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref data);
    }

    public void Refresh()
    {
        if (_added) Show();
    }

    /// <summary>Shows a native menu at the cursor and returns the chosen command id, or 0.</summary>
    public int ShowMenu(IReadOnlyList<(int Id, string? Text, bool Enabled)> items)
    {
        IntPtr menu = Win32.CreatePopupMenu();
        try
        {
            foreach (var (id, text, enabled) in items)
            {
                if (text is null) Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
                else Win32.AppendMenu(menu, Win32.MF_STRING | (enabled ? 0 : Win32.MF_GRAYED), (UIntPtr)id, text);
            }
            Win32.GetCursorPos(out var pt);
            // Without this the menu doesn't close when you click elsewhere.
            Win32.SetForegroundWindow(_host.Handle);
            int chosen = Win32.TrackPopupMenuEx(menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN | Win32.TPM_RIGHTALIGN, pt.X, pt.Y, _host.Handle, IntPtr.Zero);
            Win32.PostMessage(_host.Handle, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return chosen;
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = MakeData();
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    private Win32.NOTIFYICONDATA MakeData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _host.Handle,
        uID = IconId,
        uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP,
        uCallbackMessage = HostWindow.TrayCallbackMessage,
        hIcon = _icon,
        szTip = Tooltip.Length > 127 ? Tooltip[..127] : Tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void ReplaceIcon()
    {
        IntPtr old = _icon;
        int size = Math.Max(16, Win32.GetSystemMetrics(Win32.SM_CXSMICON));
        _icon = IconFactory.CreateTrayIcon(size, TaskbarIsLight());
        if (old != IntPtr.Zero) Win32.DestroyIcon(old);
    }

    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }
}
