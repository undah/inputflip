using System.Runtime.InteropServices;
using InputFlip.Core.Interop;

namespace InputFlip.Core.Monitors;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>A physical monitor as Windows reports it, before we know who it is.</summary>
/// <param name="HasHandle">False when Windows couldn't open the monitor for DDC/CI. A handle of 0 is valid, so don't test Handle.</param>
internal sealed record DetectedMonitor(
    bool HasHandle,
    IntPtr Handle,
    string? Description,
    string GdiDevice,
    string? DeviceInterface,
    bool IsPrimary,
    ScreenRect Bounds,
    string? Error);

internal static class MonitorEnumerator
{
    /// <summary>
    /// Lists physical monitors and opens a DDC/CI handle for each. The caller owns the handles
    /// and must release each with <see cref="NativeMethods.DestroyPhysicalMonitor"/>.
    /// </summary>
    public static List<DetectedMonitor> Enumerate()
    {
        var hMonitors = new List<IntPtr>();
        NativeMethods.MonitorEnumProc callback = (h, _, _, _) => { hMonitors.Add(h); return true; };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        var result = new List<DetectedMonitor>();
        foreach (IntPtr hMonitor in hMonitors)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            NativeMethods.GetMonitorInfo(hMonitor, ref info);
            bool primary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
            var bounds = new ScreenRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
            List<string> interfaces = GetMonitorInterfaces(info.szDevice);

            PHYSICAL_MONITOR[]? physical = null;
            string? error = null;
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count))
            {
                error = NativeMethods.LastErrorText();
            }
            else if (count == 0)
            {
                error = "Windows found no physical monitor behind this display.";
            }
            else
            {
                physical = new PHYSICAL_MONITOR[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physical))
                {
                    error = NativeMethods.LastErrorText();
                    physical = null;
                }
            }

            if (physical is null)
            {
                result.Add(new DetectedMonitor(
                    HasHandle: false,
                    Handle: IntPtr.Zero,
                    Description: null,
                    GdiDevice: info.szDevice,
                    DeviceInterface: interfaces.Count == 1 ? interfaces[0] : null,
                    IsPrimary: primary,
                    Bounds: bounds,
                    Error: error));
                continue;
            }

            for (int i = 0; i < physical.Length; i++)
            {
                result.Add(new DetectedMonitor(
                    HasHandle: true,
                    Handle: physical[i].hPhysicalMonitor,
                    Description: physical[i].szPhysicalMonitorDescription,
                    GdiDevice: info.szDevice,
                    DeviceInterface: interfaces.Count == physical.Length ? interfaces[i] : null,
                    IsPrimary: primary,
                    Bounds: bounds,
                    Error: null));
            }
        }
        return result;
    }

    private static List<string> GetMonitorInterfaces(string gdiDevice)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(gdiDevice)) return result;
        for (uint i = 0; i < 16; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!NativeMethods.EnumDisplayDevices(gdiDevice, i, ref dd, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME)) break;
            if ((dd.StateFlags & NativeMethods.DISPLAY_DEVICE_ACTIVE) != 0) result.Add(dd.DeviceID);
        }
        return result;
    }
}
