using System.Runtime.InteropServices;
using InputFlip.Core;

namespace InputFlip.Native;

/// <summary>An entry in the tray icon's menu. Id 0 with no text is a separator; Children make a submenu.</summary>
internal sealed record MenuEntry(int Id, string Text, bool Checked = false, bool Enabled = true, IReadOnlyList<MenuEntry>? Children = null)
{
    public static MenuEntry Separator { get; } = new(0, "");
}

/// <summary>
/// A hidden top-level Win32 window that receives what WinUI windows don't expose: global hotkeys, display changes,
/// sleep and wake, lock and unlock, USB devices plugging in and leaving, and the tray icon.
/// Created on the UI thread, so its messages arrive through the WinUI message loop.
/// </summary>
internal sealed class HostWindow : IDisposable
{
    private const string ClassName = "InputFlipHostWindow";
    private const uint TrayCallback = 0x8000 + 1; // WM_APP + 1
    private const uint TrayIconId = 1;

    private static Win32.WndProc? s_wndProc; // kept alive for the life of the process
    private static HostWindow? s_instance;

    private readonly HashSet<int> _hotkeyIds = new();
    private readonly uint _taskbarCreated;
    private IntPtr _deviceNotification;
    private bool _sessionNotification;
    private IntPtr _trayIcon;
    private string? _trayTooltip;

    public HostWindow()
    {
        s_instance = this;
        s_wndProc = StaticWndProc;
        IntPtr instance = Win32.GetModuleHandle(null);
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = instance,
            lpszClassName = ClassName,
        };
        Win32.RegisterClassEx(ref wc);
        Handle = Win32.CreateWindowEx(Win32.WS_EX_TOOLWINDOW, ClassName, "InputFlip", Win32.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero) Log.Error($"Creating the host window failed: {Marshal.GetLastPInvokeError()}");
        // Explorer restarting (or crashing) drops every tray icon; it says so with this message.
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    public IntPtr Handle { get; }

    public event Action<int>? HotkeyPressed;

    public event Action? DisplaysChanged;

    public event Action? Resumed;

    /// <summary>The PC is about to sleep. Handlers have a moment, not seconds.</summary>
    public event Action? Suspending;

    public event Action? SessionLocked;

    public event Action? SessionUnlocked;

    /// <summary>A USB device plugged in (true) or left (false), with its device interface path.</summary>
    public event Action<string, bool>? UsbDeviceChanged;

    /// <summary>The tray icon was clicked, or Enter was pressed on it.</summary>
    public event Action? TrayInvoked;

    /// <summary>The tray icon's menu was asked for (right-click, Shift+F10), at this screen position.</summary>
    public event Action<int, int>? TrayMenuRequested;

    /// <summary>Starts lock/unlock and USB notices. Separate from the constructor so the events are wired up first.</summary>
    public void WatchSessionAndDevices()
    {
        _sessionNotification = WTSRegisterSessionNotification(Handle, 0 /* NOTIFY_FOR_THIS_SESSION */);
        if (!_sessionNotification) Log.Warn($"Lock and unlock notices aren't available: {Marshal.GetLastPInvokeError()}");

        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = (uint)Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = Core.Interop.UsbDevices.DeviceInterface,
        };
        _deviceNotification = RegisterDeviceNotification(Handle, ref filter, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        if (_deviceNotification == IntPtr.Zero) Log.Warn($"USB notices aren't available: {Marshal.GetLastPInvokeError()}");
    }

    /// <summary>Registers a global hotkey. False if another app already owns the combination.</summary>
    public bool RegisterHotkey(int id, uint modifiers, uint vk)
    {
        if (!Win32.RegisterHotKey(Handle, id, modifiers | Win32.MOD_NOREPEAT, vk)) return false;
        _hotkeyIds.Add(id);
        return true;
    }

    public void UnregisterHotkey(int id)
    {
        Win32.UnregisterHotKey(Handle, id);
        _hotkeyIds.Remove(id);
    }

    public void UnregisterAllHotkeys()
    {
        foreach (int id in _hotkeyIds) Win32.UnregisterHotKey(Handle, id);
        _hotkeyIds.Clear();
    }

    // ---- Tray icon ----

    public bool HasTrayIcon => _trayTooltip is not null;

    /// <summary>Adds the icon by the clock, or updates its tooltip.</summary>
    public void ShowTrayIcon(string iconPath, string tooltip)
    {
        if (_trayIcon == IntPtr.Zero && File.Exists(iconPath))
            _trayIcon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, GetSystemMetrics(49 /* SM_CXSMICON */), GetSystemMetrics(50 /* SM_CYSMICON */), 0x10 /* LR_LOADFROMFILE */);
        bool adding = _trayTooltip is null;
        _trayTooltip = tooltip;
        var data = TrayData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (adding)
        {
            if (!Shell_NotifyIcon(NIM_ADD, ref data)) Log.Warn("Adding the tray icon failed");
            data.uVersion = 4; // NOTIFYICON_VERSION_4: menu and select events, with the position in wParam
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
        else
        {
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    public void HideTrayIcon()
    {
        if (_trayTooltip is null) return;
        var data = TrayData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _trayTooltip = null;
    }

    /// <summary>Shows a menu at a screen position and returns the chosen entry's id, or 0.</summary>
    public int ShowMenu(IReadOnlyList<MenuEntry> entries, int x, int y)
    {
        IntPtr menu = BuildMenu(entries, out var submenus);
        try
        {
            // Without this the menu doesn't close when you click elsewhere (documented TrackPopupMenu quirk).
            Win32.SetForegroundWindow(Handle);
            int chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, x, y, Handle, IntPtr.Zero);
            PostMessage(Handle, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            return chosen;
        }
        finally
        {
            DestroyMenu(menu); // destroys the submenus too
            _ = submenus;
        }
    }

    private static IntPtr BuildMenu(IReadOnlyList<MenuEntry> entries, out List<IntPtr> submenus)
    {
        submenus = new List<IntPtr>();
        IntPtr menu = CreatePopupMenu();
        foreach (var e in entries)
        {
            if (e.Id == 0 && e.Text.Length == 0 && e.Children is null)
            {
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                continue;
            }
            uint flags = MF_STRING | (e.Checked ? MF_CHECKED : 0) | (e.Enabled ? 0 : MF_GRAYED);
            if (e.Children is { } children)
            {
                IntPtr sub = BuildMenu(children, out var nested);
                submenus.Add(sub);
                submenus.AddRange(nested);
                AppendMenu(menu, flags | MF_POPUP, (UIntPtr)(ulong)sub, e.Text);
            }
            else
            {
                AppendMenu(menu, flags, (UIntPtr)(uint)e.Id, e.Text);
            }
        }
        return menu;
    }

    private NOTIFYICONDATA TrayData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = TrayIconId,
        uFlags = flags,
        uCallbackMessage = TrayCallback,
        hIcon = _trayIcon,
        szTip = _trayTooltip ?? "",
        szInfo = "",
        szInfoTitle = "",
    };

    // ---- Screens ----

    /// <summary>
    /// Turns all screens off, as Windows does before sleep. Video stops on every cable, but the monitors stay
    /// connected, so DDC/CI handles keep working. Any mouse or keyboard input turns them back on.
    /// </summary>
    public void TurnScreensOff() =>
        // WM_SYSCOMMAND / SC_MONITORPOWER / 2 (off), handled by DefWindowProc for our own window.
        PostMessage(Handle, 0x0112, (IntPtr)0xF170, (IntPtr)2);

    /// <summary>Wakes the screens with a one-pixel mouse nudge and back.</summary>
    public static void WakeScreens()
    {
        mouse_event(0x0001, 1, 0, 0, UIntPtr.Zero);
        mouse_event(0x0001, -1, 0, 0, UIntPtr.Zero);
    }

    public void Dispose()
    {
        UnregisterAllHotkeys();
        HideTrayIcon();
        if (_deviceNotification != IntPtr.Zero) UnregisterDeviceNotification(_deviceNotification);
        if (_sessionNotification) WTSUnRegisterSessionNotification(Handle);
        if (_trayIcon != IntPtr.Zero) DestroyIcon(_trayIcon);
        if (Handle != IntPtr.Zero) Win32.DestroyWindow(Handle);
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = s_instance;
        if (self is not null)
        {
            try
            {
                if (self.HandleMessage(msg, wParam, lParam)) return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Log.Error($"Handling window message 0x{msg:X4} failed", ex);
            }
        }
        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_HOTKEY:
                HotkeyPressed?.Invoke((int)wParam);
                return true;
            case Win32.WM_DISPLAYCHANGE:
                DisplaysChanged?.Invoke();
                return false;
            case Win32.WM_POWERBROADCAST when (int)wParam == Win32.PBT_APMRESUMEAUTOMATIC:
                Resumed?.Invoke();
                return false;
            case Win32.WM_POWERBROADCAST when (int)wParam == PBT_APMSUSPEND:
                Suspending?.Invoke();
                return false;
            case WM_WTSSESSION_CHANGE when (int)wParam == WTS_SESSION_LOCK:
                SessionLocked?.Invoke();
                return false;
            case WM_WTSSESSION_CHANGE when (int)wParam == WTS_SESSION_UNLOCK:
                SessionUnlocked?.Invoke();
                return false;
            case WM_DEVICECHANGE when (int)wParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE && lParam != IntPtr.Zero:
                OnDeviceChange((int)wParam == DBT_DEVICEARRIVAL, lParam);
                return false;
            case TrayCallback:
                OnTray((uint)((long)lParam & 0xFFFF), wParam);
                return true;
            default:
                if (msg == _taskbarCreated && _taskbarCreated != 0 && _trayTooltip is { } tip)
                {
                    _trayTooltip = null;
                    ShowTrayIcon("", tip); // the icon handle is kept, so no path is needed
                }
                return false;
        }
    }

    private void OnDeviceChange(bool arrived, IntPtr lParam)
    {
        // DEV_BROADCAST_HDR: size, device type, reserved. Only device interfaces carry a path.
        if (Marshal.ReadInt32(lParam, 4) != DBT_DEVTYP_DEVICEINTERFACE) return;
        // DEV_BROADCAST_DEVICEINTERFACE: the header (12 bytes), the class GUID (16), then the path.
        string? path = Marshal.PtrToStringUni(lParam + 28);
        if (!string.IsNullOrEmpty(path)) UsbDeviceChanged?.Invoke(path, arrived);
    }

    private void OnTray(uint message, IntPtr wParam)
    {
        // With NOTIFYICON_VERSION_4, wParam holds the position the event happened at.
        int x = unchecked((short)((long)wParam & 0xFFFF)), y = unchecked((short)(((long)wParam >> 16) & 0xFFFF));
        switch (message)
        {
            case NIN_SELECT or NIN_KEYSELECT:
                TrayInvoked?.Invoke();
                break;
            case WM_CONTEXTMENU:
                TrayMenuRequested?.Invoke(x, y);
                break;
        }
    }

    // ---- Win32 ----

    private const int PBT_APMSUSPEND = 0x0004;
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const int WTS_SESSION_LOCK = 0x7, WTS_SESSION_UNLOCK = 0x8;
    private const uint WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RIGHTALIGN = 0x8, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public uint dbcc_size;
        public uint dbcc_devicetype;
        public uint dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DEV_BROADCAST_DEVICEINTERFACE filter, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
