<#
.SYNOPSIS
    Tests the "quiet link" trick: does the main monitor accept a DDC/CI input switch over DisplayPort
    while Windows has turned the screens off (no picture on the cable)?

.DESCRIPTION
    Background (docs/plan.md, stage 1): over DisplayPort the owner's MSI G27CQ4 acts on input commands
    only while it isn't showing DisplayPort. This script turns all screens off the way Windows does
    before sleep (SC_MONITORPOWER), sends the input command a few times, wakes the screens and reads
    the input back.

    All screens go black for a few seconds. Don't touch the mouse or keyboard until they come back:
    any input wakes them early.

.PARAMETER Target
    Input value to switch the main monitor to. Default 0x12 (HDMI 2, the PS5).

.PARAMETER OffSeconds
    How long to wait after turning the screens off before sending the command.

.PARAMETER Delay
    Countdown before starting, in seconds.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\quiet-link-test.ps1
#>
param(
    [uint32]$Target = 0x12,
    [int]$OffSeconds = 3,
    [int]$Delay = 5
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

if (-not ('QuietLink' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class QuietLink
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public int L, T, R, B;
        public int WL, WT, WR, WB;
        public uint Flags;
    }

    public delegate bool EnumProc(IntPtr hMonitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumProc cb, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] a);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint current, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitor(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    // Physical monitor handle of the main display. A handle of 0 is valid, so success is returned separately.
    public static bool TryGetPrimary(out IntPtr handle, out string description)
    {
        IntPtr primary = IntPtr.Zero;
        bool seen = false;
        EnumProc cb = (h, dc, r, d) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(h, ref mi) && (mi.Flags & 1) != 0) { primary = h; seen = true; }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        handle = IntPtr.Zero;
        description = null;
        uint n;
        if (!seen || !GetNumberOfPhysicalMonitorsFromHMONITOR(primary, out n) || n == 0) return false;
        var a = new PHYSICAL_MONITOR[n];
        if (!GetPhysicalMonitorsFromHMONITOR(primary, n, a)) return false;
        handle = a[0].Handle;
        description = a[0].Description;
        return true;
    }

    public static string Set(IntPtr h, uint value)
    {
        return SetVCPFeature(h, 0x60, value) ? "accepted" : "failed: " + LastError();
    }

    public static string Read(IntPtr h)
    {
        uint cur, max;
        return GetVCPFeatureAndVCPFeatureReply(h, 0x60, IntPtr.Zero, out cur, out max)
            ? "0x" + cur.ToString("X2")
            : "no answer (" + LastError() + ")";
    }

    public static void Release(IntPtr h) { DestroyPhysicalMonitor(h); }

    static string LastError()
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code).Message + " 0x" + code.ToString("X8");
    }
}
'@
}

function Stamp { (Get-Date).ToString('HH:mm:ss.fff') }

$handle = [IntPtr]::Zero
$desc = $null
if (-not [QuietLink]::TryGetPrimary([ref]$handle, [ref]$desc)) {
    Write-Host "Couldn't open the main display for DDC/CI." -ForegroundColor Red
    exit 1
}
Write-Host ("{0}  Main display: {1}. Input now: {2}" -f (Stamp), $desc, [QuietLink]::Read($handle))
Write-Host ("Target: 0x{0:X2}. Screens go black in {1} s. Don't touch the mouse or keyboard until they come back." -f $Target, $Delay)
for ($i = $Delay; $i -ge 1; $i--) { Start-Sleep -Seconds 1 }

# WM_SYSCOMMAND / SC_MONITORPOWER / 2 = off, handled by DefWindowProc of our own (hidden) window.
$form = New-Object System.Windows.Forms.Form
$null = $form.Handle
Write-Host ("{0}  Screens off" -f (Stamp))
[void][QuietLink]::SendMessage($form.Handle, 0x0112, [IntPtr]0xF170, [IntPtr]2)
Start-Sleep -Seconds $OffSeconds

$results = @()
for ($i = 1; $i -le 6; $i++) {
    $r = [QuietLink]::Set($handle, $Target)
    $results += ("{0}  switch command {1}: {2}" -f (Stamp), $i, $r)
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Milliseconds 1500

# Any input wakes the screens: nudge the mouse by one pixel and back.
[QuietLink]::mouse_event(0x0001, 1, 0, 0, [UIntPtr]::Zero)
[QuietLink]::mouse_event(0x0001, -1, 0, 0, [UIntPtr]::Zero)
$results += ("{0}  Screens woken" -f (Stamp))
Start-Sleep -Seconds 4

$results | ForEach-Object { Write-Host $_ }
for ($i = 1; $i -le 3; $i++) {
    Write-Host ("{0}  Input read {1}: {2}" -f (Stamp), $i, [QuietLink]::Read($handle))
    Start-Sleep -Milliseconds 500
}
[QuietLink]::Release($handle)
$form.Dispose()
