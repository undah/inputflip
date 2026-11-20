using System.Runtime.InteropServices;
using Switchboard.Core;

namespace Switchboard.Native;

/// <summary>
/// A hidden top-level Win32 window that receives what WinUI windows don't expose:
/// global hotkeys, display changes and resume from sleep.
/// Created on the UI thread, so its messages arrive through the WinUI message loop.
/// </summary>
internal sealed class HostWindow : IDisposable
{
    private const string ClassName = "SwitchboardHostWindow";
    private static Win32.WndProc? s_wndProc; // kept alive for the life of the process
    private static HostWindow? s_instance;

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
    }

    public IntPtr Handle { get; }

    public event Action<int>? HotkeyPressed;

    public event Action? DisplaysChanged;

    public event Action? Resumed;

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

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);

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
            case Win32.WM_HOTKEY:
                HotkeyPressed?.Invoke((int)wParam);
                return true;
            case Win32.WM_DISPLAYCHANGE:
                DisplaysChanged?.Invoke();
                return false;
            case Win32.WM_POWERBROADCAST when (int)wParam == Win32.PBT_APMRESUMEAUTOMATIC:
                Resumed?.Invoke();
                return false;
            default:
                return false;
        }
    }
}
