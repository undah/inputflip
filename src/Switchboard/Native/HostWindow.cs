using System.Runtime.InteropServices;
using Switchboard.Core;

namespace Switchboard.Native;

/// <summary>
/// A hidden top-level Win32 window that receives what WinUI windows don't expose: tray icon clicks,
/// global hotkeys, display changes, resume from sleep and taskbar theme changes.
/// Created on the UI thread, so its messages arrive through the WinUI message loop.
/// </summary>
internal sealed class HostWindow : IDisposable
{
    public const uint TrayCallbackMessage = Win32.WM_APP + 1;

    private const string ClassName = "SwitchboardHostWindow";
    private static Win32.WndProc? s_wndProc; // kept alive for the life of the process
    private static HostWindow? s_instance;

    private readonly uint _taskbarCreatedMessage;
    private readonly HashSet<int> _hotkeyIds = new();

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
        Handle = Win32.CreateWindowEx(Win32.WS_EX_TOOLWINDOW, ClassName, "Switchboard", Win32.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero) Log.Error($"Creating the host window failed: {Marshal.GetLastPInvokeError()}");
        _taskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");
    }

    public IntPtr Handle { get; }

    /// <summary>Tray icon event (NIN_SELECT, NIN_KEYSELECT or WM_CONTEXTMENU) with the click position.</summary>
    public event Action<int, int, int>? TrayEvent;

    public event Action<int>? HotkeyPressed;

    public event Action? DisplaysChanged;

    public event Action? Resumed;

    public event Action? TaskbarCreated;

    public event Action? ThemeChanged;

    /// <summary>Registers a global hotkey. False if another app already owns the combination.</summary>
    public bool RegisterHotkey(int id, uint modifiers, uint vk)
    {
        if (!Win32.RegisterHotKey(Handle, id, modifiers | Win32.MOD_NOREPEAT, vk)) return false;
        _hotkeyIds.Add(id);
        return true;
    }

    public void UnregisterAllHotkeys()
    {
        foreach (int id in _hotkeyIds) Win32.UnregisterHotKey(Handle, id);
        _hotkeyIds.Clear();
    }

    public void Dispose()
    {
        UnregisterAllHotkeys();
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
            case TrayCallbackMessage:
                // NOTIFYICON_VERSION_4: event in the low word of lParam, anchor point in wParam.
                TrayEvent?.Invoke(Win32.LoWord(lParam), Win32.LoWord(wParam), Win32.HiWord(wParam));
                return true;
            case Win32.WM_HOTKEY:
                HotkeyPressed?.Invoke((int)wParam);
                return true;
            case Win32.WM_DISPLAYCHANGE:
                DisplaysChanged?.Invoke();
                return false;
            case Win32.WM_POWERBROADCAST when (int)wParam == Win32.PBT_APMRESUMEAUTOMATIC:
                Resumed?.Invoke();
                return false;
            case Win32.WM_SETTINGCHANGE when lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet":
                ThemeChanged?.Invoke();
                return false;
            default:
                if (msg == _taskbarCreatedMessage)
                {
                    TaskbarCreated?.Invoke();
                    return false;
                }
                return false;
        }
    }
}
