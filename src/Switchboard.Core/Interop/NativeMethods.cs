using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Switchboard.Core.Interop;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PHYSICAL_MONITOR
{
    public IntPtr hPhysicalMonitor;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szPhysicalMonitorDescription;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAY_DEVICE
{
    public int cb;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceString;

    public uint StateFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceID;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceKey;
}

internal static class NativeMethods
{
    public const uint MONITORINFOF_PRIMARY = 1;
    public const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
    public const uint DISPLAY_DEVICE_ACTIVE = 1;

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE info, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint length);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, [Out] byte[] buffer, uint length);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr codeType, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);

    /// <summary>Last Win32 error from a SetLastError P/Invoke, as text for the log (never for the UI).</summary>
    public static string LastErrorText()
    {
        int code = Marshal.GetLastPInvokeError();
        return $"{new Win32Exception(code).Message} (0x{code:X8})";
    }
}
