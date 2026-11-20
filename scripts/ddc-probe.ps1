<#
.SYNOPSIS
    Stage 1 DDC/CI probe for Switchboard. Finds monitors, reads their inputs,
    and can test switching to another input and back.

.DESCRIPTION
    Discovery (the default) only reads from the monitor; nothing on screen
    changes. For each monitor it prints the model, reads the DDC/CI
    capabilities string, lists the inputs advertised for VCP code 0x60
    (Input Source), and reads the current input.

    Switch test (-SwitchTo) switches one monitor to another input, waits,
    switches it back, and reports whether that worked and how long each step
    took. The screen WILL leave this PC for a few seconds. If it doesn't come
    back on its own, use the input button on the monitor.

    Talks to dxva2.dll through Add-Type, so there is nothing to build or
    install.

.PARAMETER Monitor
    Which monitor to switch, by the number discovery prints. Optional when
    there's only one monitor.

.PARAMETER SwitchTo
    Input value to switch to, for example 0x11 (HDMI 1) or 17. Run discovery
    first to see the values your monitor lists.

.PARAMETER WaitSeconds
    How long to stay on the other input before switching back. Default 5.

.PARAMETER From
    The input this PC is on, used to switch back when the monitor won't
    report its current input (some monitors answer only some of the time).
    For example 0x0F for DisplayPort 1.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\ddc-probe.ps1

    Discovery only. Nothing on screen changes.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\ddc-probe.ps1 -Monitor 1 -SwitchTo 0x11

    Switch monitor 1 to HDMI 1 for 5 seconds, then back.

.EXAMPLE
    .\scripts\ddc-probe.ps1 -Monitor 1 -SwitchTo 0x11 -WhatIf

    Walk through the switch test without sending any switch commands.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int]$Monitor = 0,

    [ValidateRange(1, 255)]
    [int]$SwitchTo,

    [ValidateRange(1, 60)]
    [int]$WaitSeconds = 5,

    [ValidateRange(1, 255)]
    [int]$From
)

$ErrorActionPreference = 'Stop'
$script:Cmdlet = $PSCmdlet

# ---------------------------------------------------------------------------
# Native interop and capabilities-string parser (compiled once per session)
# ---------------------------------------------------------------------------

$source = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SwitchboardProbe
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
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

    internal static class Native
    {
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
        public static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint length);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, [Out] byte[] buffer, uint length);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr codeType, out uint current, out uint maximum);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);
    }

    public class MonitorEntry
    {
        public int Index;
        public bool HasHandle;           // false when Windows couldn't open the monitor (see Error)
        public IntPtr Handle;            // physical monitor handle for DDC/CI; 0 is a valid handle
        public string Description;       // from dxva2, often just "Generic PnP Monitor"
        public string GdiDevice;         // e.g. \\.\DISPLAY1
        public string DeviceInterface;   // e.g. \\?\DISPLAY#GSM5B7F#...#{guid}, used to match the EDID
        public bool IsPrimary;
        public string Error;             // why there is no handle
    }

    public class VcpResult
    {
        public bool Ok;
        public uint Current;
        public uint Maximum;
        public long Ms;                  // total time, including retries
        public int Attempts;
        public string Error;
    }

    public class CapsResult
    {
        public bool Ok;
        public string Text;
        public long Ms;
        public int Attempts;
        public string Error;
    }

    public static class Ddc
    {
        private static readonly List<PHYSICAL_MONITOR[]> Owned = new List<PHYSICAL_MONITOR[]>();

        public static List<MonitorEntry> Enumerate()
        {
            var handles = new List<IntPtr>();
            Native.MonitorEnumProc callback = delegate(IntPtr h, IntPtr dc, IntPtr r, IntPtr d) { handles.Add(h); return true; };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            var result = new List<MonitorEntry>();
            foreach (IntPtr hMonitor in handles)
            {
                var info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                Native.GetMonitorInfo(hMonitor, ref info);
                bool primary = (info.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
                List<string> interfaces = GetMonitorInterfaces(info.szDevice);

                uint count;
                PHYSICAL_MONITOR[] physical = null;
                string error = null;
                if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count))
                {
                    error = LastError();
                }
                else if (count == 0)
                {
                    error = "Windows found no physical monitor behind this display.";
                }
                else
                {
                    physical = new PHYSICAL_MONITOR[count];
                    if (Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physical))
                    {
                        Owned.Add(physical);
                    }
                    else
                    {
                        error = LastError();
                        physical = null;
                    }
                }

                if (physical == null)
                {
                    var failed = new MonitorEntry();
                    failed.Index = result.Count + 1;
                    failed.Handle = IntPtr.Zero;
                    failed.GdiDevice = info.szDevice;
                    failed.IsPrimary = primary;
                    failed.DeviceInterface = interfaces.Count == 1 ? interfaces[0] : null;
                    failed.Error = error;
                    result.Add(failed);
                    continue;
                }

                for (int i = 0; i < physical.Length; i++)
                {
                    var entry = new MonitorEntry();
                    entry.Index = result.Count + 1;
                    entry.HasHandle = true;
                    entry.Handle = physical[i].hPhysicalMonitor;
                    entry.Description = physical[i].szPhysicalMonitorDescription;
                    entry.GdiDevice = info.szDevice;
                    entry.IsPrimary = primary;
                    entry.DeviceInterface = interfaces.Count == physical.Length ? interfaces[i] : null;
                    result.Add(entry);
                }
            }
            return result;
        }

        public static void ReleaseAll()
        {
            foreach (PHYSICAL_MONITOR[] group in Owned)
            {
                Native.DestroyPhysicalMonitors((uint)group.Length, group);
            }
            Owned.Clear();
        }

        public static CapsResult ReadCapabilities(IntPtr monitor, int maxAttempts)
        {
            var r = new CapsResult();
            var clock = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                r.Attempts = attempt;
                uint length;
                if (!Native.GetCapabilitiesStringLength(monitor, out length))
                {
                    r.Error = LastError();
                }
                else if (length == 0)
                {
                    r.Error = "The monitor returned an empty capabilities string.";
                }
                else
                {
                    var buffer = new byte[length];
                    if (!Native.CapabilitiesRequestAndCapabilitiesReply(monitor, buffer, length))
                    {
                        r.Error = LastError();
                    }
                    else
                    {
                        int end = Array.IndexOf(buffer, (byte)0);
                        if (end < 0) end = buffer.Length;
                        r.Ok = true;
                        r.Text = Encoding.ASCII.GetString(buffer, 0, end);
                        r.Error = null;
                        break;
                    }
                }
                if (attempt < maxAttempts) Thread.Sleep(200);
            }
            r.Ms = clock.ElapsedMilliseconds;
            return r;
        }

        public static VcpResult GetVcp(IntPtr monitor, byte code, int maxAttempts)
        {
            var r = new VcpResult();
            var clock = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                r.Attempts = attempt;
                uint current, maximum;
                if (Native.GetVCPFeatureAndVCPFeatureReply(monitor, code, IntPtr.Zero, out current, out maximum))
                {
                    r.Ok = true;
                    r.Current = current;
                    r.Maximum = maximum;
                    r.Error = null;
                    break;
                }
                r.Error = LastError();
                if (attempt < maxAttempts) Thread.Sleep(100);
            }
            r.Ms = clock.ElapsedMilliseconds;
            return r;
        }

        public static VcpResult SetVcp(IntPtr monitor, byte code, uint value, int maxAttempts)
        {
            var r = new VcpResult();
            var clock = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                r.Attempts = attempt;
                if (Native.SetVCPFeature(monitor, code, value))
                {
                    r.Ok = true;
                    r.Current = value;
                    r.Error = null;
                    break;
                }
                r.Error = LastError();
                if (attempt < maxAttempts) Thread.Sleep(100);
            }
            r.Ms = clock.ElapsedMilliseconds;
            return r;
        }

        private static List<string> GetMonitorInterfaces(string gdiDevice)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(gdiDevice)) return result;
            for (uint i = 0; i < 16; i++)
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!Native.EnumDisplayDevices(gdiDevice, i, ref dd, 1)) break; // EDD_GET_DEVICE_INTERFACE_NAME
                if ((dd.StateFlags & 1) != 0) result.Add(dd.DeviceID);       // DISPLAY_DEVICE_ACTIVE
            }
            return result;
        }

        private static string LastError()
        {
            int code = Marshal.GetLastWin32Error();
            return new Win32Exception(code).Message + " (0x" + code.ToString("X8") + ")";
        }
    }

    public class VcpCode
    {
        public byte Code;
        public List<uint> Values; // null when the code has no value list
    }

    // Parses MCCS capabilities strings such as
    // (prot(monitor)type(LCD)model(X)cmds(01 02 03)vcp(02 10 12 60(0F 11 12) D6(01 04))mccs_ver(2.2))
    public static class CapsParser
    {
        // Contents of the first "<name>(...)" group, or null.
        public static string GetGroup(string caps, string name)
        {
            if (string.IsNullOrEmpty(caps)) return null;
            int from = 0;
            while (true)
            {
                int at = caps.IndexOf(name + "(", from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return null;
                if (at > 0 && (char.IsLetterOrDigit(caps[at - 1]) || caps[at - 1] == '_'))
                {
                    from = at + 1; // part of a longer name, e.g. "vcpname(" when looking for "name("
                    continue;
                }
                int start = at + name.Length + 1;
                int depth = 1;
                for (int i = start; i < caps.Length; i++)
                {
                    if (caps[i] == '(') depth++;
                    else if (caps[i] == ')' && --depth == 0) return caps.Substring(start, i - start);
                }
                return caps.Substring(start); // unbalanced; take the rest
            }
        }

        // Codes and value lists from the inside of a vcp(...) group.
        public static List<VcpCode> ParseVcp(string vcpGroup)
        {
            var result = new List<VcpCode>();
            if (vcpGroup == null) return result;
            int i = 0, n = vcpGroup.Length;
            while (i < n)
            {
                if (!IsHex(vcpGroup[i])) { i++; continue; }
                int start = i;
                while (i < n && i - start < 2 && IsHex(vcpGroup[i])) i++;
                var entry = new VcpCode();
                entry.Code = Convert.ToByte(vcpGroup.Substring(start, i - start), 16);
                int j = i;
                while (j < n && vcpGroup[j] == ' ') j++;
                if (j < n && vcpGroup[j] == '(')
                {
                    int depth = 1, valuesStart = j + 1, k = valuesStart;
                    while (k < n && depth > 0)
                    {
                        if (vcpGroup[k] == '(') depth++;
                        else if (vcpGroup[k] == ')') depth--;
                        k++;
                    }
                    int valuesEnd = depth == 0 ? k - 1 : k;
                    entry.Values = ParseHexList(vcpGroup.Substring(valuesStart, valuesEnd - valuesStart));
                    i = k;
                }
                result.Add(entry);
            }
            return result;
        }

        // Anything that isn't a hex digit separates values; run-together digits are split into pairs.
        private static List<uint> ParseHexList(string text)
        {
            var values = new List<uint>();
            var token = new StringBuilder();
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && IsHex(text[i])) { token.Append(text[i]); continue; }
                if (token.Length == 0) continue;
                string t = token.ToString();
                token.Length = 0;
                if (t.Length <= 2) { values.Add(Convert.ToUInt32(t, 16)); continue; }
                for (int p = 0; p + 1 < t.Length; p += 2) values.Add(Convert.ToUInt32(t.Substring(p, 2), 16));
            }
            return values;
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
'@

if (-not ('SwitchboardProbe.Ddc' -as [type])) {
    Add-Type -TypeDefinition $source -Language CSharp
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

$VcpInputSource = [byte]0x60

# Standard MCCS values for VCP 0x60. 0x1B is common for USB-C but not in MCCS.
$script:InputNames = @{
    0x01 = 'VGA 1';        0x02 = 'VGA 2'
    0x03 = 'DVI 1';        0x04 = 'DVI 2'
    0x05 = 'Composite 1';  0x06 = 'Composite 2'
    0x07 = 'S-Video 1';    0x08 = 'S-Video 2'
    0x09 = 'Tuner 1';      0x0A = 'Tuner 2';      0x0B = 'Tuner 3'
    0x0C = 'Component 1';  0x0D = 'Component 2';  0x0E = 'Component 3'
    0x0F = 'DisplayPort 1'; 0x10 = 'DisplayPort 2'
    0x11 = 'HDMI 1';       0x12 = 'HDMI 2'
    0x1B = 'USB-C'
}

function Format-HexValue([uint32]$Value) {
    if ($Value -gt 0xFF) { '0x{0:X4}' -f $Value } else { '0x{0:X2}' -f $Value }
}

function Format-Input([uint32]$Value) {
    $low = [int]($Value -band 0xFF)
    $name = $script:InputNames[$low]
    if ($Value -gt 0xFF) {
        if ($name) { return '{0} (low byte {1} = {2})' -f (Format-HexValue $Value), (Format-HexValue $low), $name }
        return '{0} (not a standard value)' -f (Format-HexValue $Value)
    }
    if ($name) { return '{0} ({1})' -f (Format-HexValue $Value), $name }
    '{0} (not a standard value)' -f (Format-HexValue $Value)
}

function Get-ValueKind([uint32]$Value) {
    if ($Value -ge 0x01 -and $Value -le 0x12) { return 'standard' }
    if ($Value -eq 0x1B) { return 'common' }
    'vendor'
}

function Convert-EdidString($Codes) {
    if (-not $Codes) { return '' }
    (-join ($Codes | Where-Object { $_ -ne 0 } | ForEach-Object { [char][int]$_ })).Trim()
}

# EDID identity per monitor, keyed by WMI instance name (DISPLAY\GSM5B7F\...&UID4353_0).
function Get-EdidMap {
    $WhatIfPreference = $false  # keep -WhatIf from echoing the CIM module's alias setup
    $map = @{}
    try {
        foreach ($m in Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction Stop) {
            $map[$m.InstanceName.ToUpperInvariant()] = [pscustomobject]@{
                Manufacturer = Convert-EdidString $m.ManufacturerName
                Product      = Convert-EdidString $m.ProductCodeID
                Name         = Convert-EdidString $m.UserFriendlyName
                Serial       = Convert-EdidString $m.SerialNumberID
                Connection   = $null
            }
        }
    } catch {
        # WMI isn't essential for the probe; fall back to the capabilities model name.
    }
    try {
        # DDC/CI runs over different wiring per cable type, so it helps to know which one is in use.
        foreach ($c in Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorConnectionParams -ErrorAction Stop) {
            $entry = $map[$c.InstanceName.ToUpperInvariant()]
            if (-not $entry) { continue }
            $entry.Connection = switch ([int64]$c.VideoOutputTechnology) {
                0          { 'VGA' }
                4          { 'DVI' }
                5          { 'HDMI' }
                10         { 'DisplayPort' }
                11         { 'DisplayPort (embedded)' }
                2147483648 { 'built-in panel' }
                default    { 'type {0}' -f $_ }
            }
        }
    } catch {
    }
    $map
}

function Find-Edid($Map, [string]$DeviceInterface) {
    if (-not $DeviceInterface) { return $null }
    # \\?\DISPLAY#GSM5B7F#5&2f7a&0&UID4353#{e6f07b5f-...}  ->  DISPLAY\GSM5B7F\5&2f7a&0&UID4353
    $id = $DeviceInterface
    if ($id.StartsWith('\\?\')) { $id = $id.Substring(4) }
    $brace = $id.IndexOf('#{')
    if ($brace -ge 0) { $id = $id.Substring(0, $brace) }
    $id = $id.Replace('#', '\').ToUpperInvariant()
    foreach ($key in $Map.Keys) {
        if ($key.StartsWith($id + '_')) { return $Map[$key] }
    }
    $null
}

function Get-MonitorName($Entry, $Edid, [string]$CapsModel) {
    if ($Edid -and $Edid.Name) { return $Edid.Name }
    if ($CapsModel) { return $CapsModel }
    if ($Entry.Description) { return $Entry.Description }
    'Unknown monitor'
}

function Read-Answer([string]$Question) {
    try { $answer = Read-Host $Question } catch { return $null }
    if ($null -eq $answer) { return $null }
    switch ($answer.Trim().ToLowerInvariant()) {
        { $_ -in 'y', 'yes' } { return 'yes' }
        { $_ -in 'n', 'no' }  { return 'no' }
    }
    $null
}

function Send-Input($Entry, [uint32]$Value) {
    $label = 'Monitor {0}' -f $Entry.Index
    if (-not $script:Cmdlet.ShouldProcess($label, ('Switch input to {0}' -f (Format-Input $Value)))) {
        $skipped = New-Object SwitchboardProbe.VcpResult
        $skipped.Ok = $true
        $skipped.Error = 'skipped (-WhatIf)'
        return $skipped
    }
    [SwitchboardProbe.Ddc]::SetVcp($Entry.Handle, $VcpInputSource, $Value, 1)
}

# ---------------------------------------------------------------------------
# Discovery
# ---------------------------------------------------------------------------

function Invoke-Discovery($Monitors, $EdidMap) {
    Write-Host 'Switchboard DDC/CI probe: discovery (read-only, nothing on screen changes)'
    Write-Host ('Windows reports {0} monitor(s).' -f $Monitors.Count)
    $suggestion = $null

    foreach ($m in $Monitors) {
        $edid = Find-Edid $EdidMap $m.DeviceInterface
        $primary = if ($m.IsPrimary) { ' (primary)' } else { '' }
        Write-Host ''

        if (-not $m.HasHandle) {
            Write-Host ('Monitor {0}: {1}{2}' -f $m.Index, (Get-MonitorName $m $edid $null), $primary)
            Write-Host ("  Windows couldn't open this monitor for DDC/CI: {0}" -f $m.Error) -ForegroundColor Yellow
            continue
        }

        $caps = [SwitchboardProbe.Ddc]::ReadCapabilities($m.Handle, 3)
        $capsModel = if ($caps.Ok) { [SwitchboardProbe.CapsParser]::GetGroup($caps.Text, 'model') } else { $null }
        Write-Host ('Monitor {0}: {1}{2}' -f $m.Index, (Get-MonitorName $m $edid $capsModel), $primary)
        if ($edid) {
            Write-Host ('  EDID:           maker {0}, product {1}, serial {2}' -f $edid.Manufacturer, $edid.Product, $(if ($edid.Serial) { $edid.Serial } else { '(none)' }))
        }
        Write-Host ('  Windows:        {0} on {1}' -f $m.Description, $m.GdiDevice)
        if ($edid -and $edid.Connection) { Write-Host ('  Connected via:  {0}' -f $edid.Connection) }

        # Capabilities string and the inputs it lists
        $listed = @()
        if ($caps.Ok) {
            Write-Host ('  Capabilities:   read in {0} ms ({1} attempt(s))' -f $caps.Ms, $caps.Attempts)
            $mccs = [SwitchboardProbe.CapsParser]::GetGroup($caps.Text, 'mccs_ver')
            if ($mccs) { Write-Host ('  MCCS version:   {0}' -f $mccs) }
            $codes = [SwitchboardProbe.CapsParser]::ParseVcp([SwitchboardProbe.CapsParser]::GetGroup($caps.Text, 'vcp'))
            $inputCode = $codes | Where-Object { $_.Code -eq 0x60 } | Select-Object -First 1
            if (-not $inputCode) {
                Write-Host "  Inputs listed:  none (the capabilities string doesn't mention VCP 60)" -ForegroundColor Yellow
            } elseif (-not $inputCode.Values -or $inputCode.Values.Count -eq 0) {
                Write-Host "  Inputs listed:  VCP 60 is supported, but no input values are listed" -ForegroundColor Yellow
            } else {
                $listed = @($inputCode.Values | ForEach-Object { [uint32]$_ })
                Write-Host '  Inputs listed:'
                foreach ($v in $listed) { Write-Host ('    {0}' -f (Format-Input $v)) }
                $nonStandard = @($listed | Where-Object { (Get-ValueKind $_) -ne 'standard' })
                if ($nonStandard.Count -eq 0) {
                    Write-Host '  Value type:     all standard MCCS values'
                } else {
                    Write-Host ('  Value type:     not all standard; check {0}' -f (($nonStandard | ForEach-Object { Format-HexValue $_ }) -join ', ')) -ForegroundColor Yellow
                }
            }
        } else {
            Write-Host ("  Capabilities:   no reply after {0} attempt(s): {1}" -f $caps.Attempts, $caps.Error) -ForegroundColor Yellow
        }

        # Current input
        $current = [SwitchboardProbe.Ddc]::GetVcp($m.Handle, $VcpInputSource, 3)
        if ($current.Ok) {
            $currentLow = [uint32]($current.Current -band 0xFF)
            Write-Host ('  Current input:  {0}, read in {1} ms ({2} attempt(s)), maximum reported {3}' -f (Format-Input $current.Current), $current.Ms, $current.Attempts, (Format-HexValue $current.Maximum))
            if ($listed.Count -gt 0 -and $listed -notcontains $currentLow) {
                Write-Host "  Note:           the current input isn't in the listed values, so this monitor may use its own numbering" -ForegroundColor Yellow
            }
            # Suggest a switch test: primary monitor first, and a digital input (DP/HDMI/USB-C) over VGA.
            if ((-not $suggestion -or $m.IsPrimary) -and $listed.Count -gt 0) {
                $others = @($listed | Where-Object { $_ -ne $currentLow })
                $digital = @($others | Where-Object { ($_ -ge 0x0F -and $_ -le 0x12) -or $_ -eq 0x1B })
                $other = if ($digital.Count -gt 0) { $digital[0] } elseif ($others.Count -gt 0) { $others[0] } else { $null }
                if ($null -ne $other) { $suggestion = '-Monitor {0} -SwitchTo {1}' -f $m.Index, (Format-HexValue $other) }
            }
        } else {
            Write-Host ("  Current input:  no reply after {0} attempt(s): {1}" -f $current.Attempts, $current.Error) -ForegroundColor Yellow
        }

        if ($caps.Ok -or $current.Ok) {
            Write-Host '  DDC/CI:         responds' -ForegroundColor Green
        } else {
            Write-Host "  DDC/CI:         no response. Check that DDC/CI is on in the monitor's on-screen menu." -ForegroundColor Red
            Write-Host "                  Laptop screens, most TVs and some docks or adapters don't support it."
        }

        if ($caps.Ok) {
            Write-Host '  Raw capabilities string:'
            Write-Host ('    {0}' -f $caps.Text)
        }
    }

    Write-Host ''
    if ($suggestion) {
        Write-Host 'Next: test switching away and back (the screen will leave this PC for a few seconds):'
        Write-Host ('  powershell -ExecutionPolicy Bypass -File .\scripts\ddc-probe.ps1 {0}' -f $suggestion)
    }
}

# ---------------------------------------------------------------------------
# Switch test
# ---------------------------------------------------------------------------

function Invoke-SwitchTest($Monitors, $EdidMap, [int]$Pick, [uint32]$Target, [int]$Wait, [int]$Assumed) {
    $usable = @($Monitors | Where-Object { $_.HasHandle })
    $m = $null
    if ($Pick -gt 0) {
        $m = $Monitors | Where-Object { $_.Index -eq $Pick } | Select-Object -First 1
        if (-not $m) {
            Write-Host ("There's no monitor {0}. Run the script without -SwitchTo to see the list." -f $Pick) -ForegroundColor Red
            return
        }
    } elseif ($usable.Count -eq 1) {
        $m = $usable[0]
    } else {
        Write-Host ('Found {0} monitors. Choose one with -Monitor <number>:' -f $usable.Count) -ForegroundColor Yellow
        foreach ($u in $usable) {
            Write-Host ('  {0}: {1}' -f $u.Index, (Get-MonitorName $u (Find-Edid $EdidMap $u.DeviceInterface) $null))
        }
        return
    }
    if (-not $m.HasHandle) {
        Write-Host ("Windows couldn't open monitor {0} for DDC/CI: {1}" -f $m.Index, $m.Error) -ForegroundColor Red
        return
    }

    $name = Get-MonitorName $m (Find-Edid $EdidMap $m.DeviceInterface) $null
    # Some monitors (the owner's MSI G27CQ4) only answer some of the time, so try harder here.
    $before = [SwitchboardProbe.Ddc]::GetVcp($m.Handle, $VcpInputSource, 10)
    if ($before.Ok) {
        $original = [uint32]($before.Current -band 0xFF)
        if ($Assumed -gt 0 -and $Assumed -ne $original) {
            Write-Host ('The monitor says it is on {0}, not {1}; using what it says.' -f (Format-Input $original), (Format-Input $Assumed)) -ForegroundColor Yellow
        }
    } elseif ($Assumed -gt 0) {
        $original = [uint32]$Assumed
        Write-Host ("Couldn't read the current input of monitor {0} after {1} tries, so using -From {2}." -f $m.Index, $before.Attempts, (Format-Input $original)) -ForegroundColor Yellow
    } else {
        Write-Host ("Couldn't read the current input of monitor {0} after {1} tries: {2}" -f $m.Index, $before.Attempts, $before.Error) -ForegroundColor Red
        Write-Host "Without it the script can't switch back, so the test didn't run. Run it again, or say which input this PC is on with -From (for example -From 0x0F for DisplayPort 1)."
        return
    }
    if ($original -eq $Target) {
        Write-Host ('Monitor {0} is already on {1}. Pick a different input.' -f $m.Index, (Format-Input $Target)) -ForegroundColor Yellow
        return
    }

    Write-Host ('Switch test on monitor {0}: {1}' -f $m.Index, $name)
    Write-Host ('  This PC:     {0}' -f (Format-Input $original))
    Write-Host ('  Switch to:   {0}' -f (Format-Input $Target))
    $deviceActive = Read-Answer ('Is a device connected to {0} and turned on? [y/n]' -f (Format-Input $Target))
    Write-Host ''
    Write-Host ('The screen will switch away for about {0} seconds, then the script switches it back.' -f $Wait) -ForegroundColor Yellow
    Write-Host "If it doesn't come back, press the input button on the monitor. Press Ctrl+C now to cancel." -ForegroundColor Yellow
    for ($i = 3; $i -ge 1; $i--) { Write-Host ('  Starting in {0}...' -f $i); Start-Sleep -Seconds 1 }

    # 1. Switch away, then watch what the monitor reports while it's on the other input.
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $away = Send-Input $m $Target
    if (-not $away.Ok) {
        Write-Host ("The monitor didn't accept the switch command: {0}" -f $away.Error) -ForegroundColor Red
        Write-Host 'Nothing changed on screen, so there is nothing to switch back.'
        return
    }
    $reportedAt = $null
    $backOnItsOwnAt = $null
    $readsOk = 0
    $readsFailed = 0
    $lastReadError = $null
    $seen = New-Object System.Collections.Generic.List[string]
    while ($clock.ElapsedMilliseconds -lt ($Wait * 1000)) {
        $r = [SwitchboardProbe.Ddc]::GetVcp($m.Handle, $VcpInputSource, 1)
        if ($r.Ok) {
            $readsOk++
            $v = [uint32]($r.Current -band 0xFF)
            $hex = Format-HexValue $v
            if (-not $seen.Contains($hex)) { $seen.Add($hex) }
            if ($v -eq $Target -and $null -eq $reportedAt) { $reportedAt = $clock.ElapsedMilliseconds }
            if ($v -eq $original -and $null -ne $reportedAt -and $null -eq $backOnItsOwnAt) { $backOnItsOwnAt = $clock.ElapsedMilliseconds }
        } else {
            $readsFailed++
            $lastReadError = $r.Error
        }
        Start-Sleep -Milliseconds 250
    }

    # 2. Switch back. Up to 3 tries, each followed by up to 4 s of read-back.
    $backClock = [Diagnostics.Stopwatch]::StartNew()
    $backConfirmedAt = $null
    $backAttempts = 0
    $backErrors = New-Object System.Collections.Generic.List[string]
    for ($attempt = 1; $attempt -le 3 -and $null -eq $backConfirmedAt; $attempt++) {
        $backAttempts = $attempt
        $s = Send-Input $m $original
        if (-not $s.Ok) { $backErrors.Add($s.Error) }
        $deadline = $backClock.ElapsedMilliseconds + 4000
        while ($backClock.ElapsedMilliseconds -lt $deadline) {
            Start-Sleep -Milliseconds 250
            $r = [SwitchboardProbe.Ddc]::GetVcp($m.Handle, $VcpInputSource, 1)
            if ($r.Ok -and ([uint32]($r.Current -band 0xFF)) -eq $original) {
                $backConfirmedAt = $backClock.ElapsedMilliseconds
                break
            }
        }
    }

    Write-Host ''
    $sawIt = Read-Answer 'Is this screen showing this PC again? [y/n]'

    # 3. Summary, in the shape of the stage 1 results table.
    $dryRun = if ($WhatIfPreference) { ' (dry run with -WhatIf: no switch commands were sent)' } else { '' }
    Write-Host ''
    Write-Host ('Switch test result{0}' -f $dryRun)
    Write-Host ('  Monitor:                 {0} (monitor {1})' -f $name, $m.Index)
    Write-Host ('  Test:                    {0} -> {1} -> back' -f (Format-Input $original), (Format-Input $Target))
    Write-Host ('  Device on other input:   {0}' -f $(if ($deviceActive) { $deviceActive } else { 'not answered' }))
    if ($null -ne $reportedAt) {
        Write-Host ('  Switch away:             command took {0} ms; monitor reported the new input after {1} ms' -f $away.Ms, $reportedAt)
    } else {
        Write-Host ('  Switch away:             command took {0} ms; monitor never reported the new input' -f $away.Ms)
    }
    $seenText = if ($seen.Count -gt 0) { '; values seen: ' + ($seen -join ', ') } else { '' }
    Write-Host ('  Reads while away:        {0} answered, {1} failed{2}' -f $readsOk, $readsFailed, $seenText)
    if ($readsFailed -gt 0 -and $lastReadError) {
        Write-Host ('                           last error: {0}' -f $lastReadError)
    }
    if ($null -ne $backOnItsOwnAt) {
        Write-Host ('  Went back on its own:    yes, after {0} ms (auto input switching?)' -f $backOnItsOwnAt)
    }
    if ($null -ne $backConfirmedAt) {
        Write-Host ('  Switch back:             confirmed on attempt {0}, {1} ms after the first command' -f $backAttempts, $backConfirmedAt) -ForegroundColor Green
    } else {
        Write-Host ('  Switch back:             not confirmed after {0} attempts' -f $backAttempts) -ForegroundColor Red
    }
    if ($backErrors.Count -gt 0) {
        Write-Host ('                           command errors: {0}' -f ($backErrors -join '; '))
    }
    Write-Host ('  You saw this PC again:   {0}' -f $(if ($sawIt) { $sawIt } else { 'not answered' }))
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

$edidMap = Get-EdidMap
$monitors = @([SwitchboardProbe.Ddc]::Enumerate())
try {
    if ($monitors.Count -eq 0) {
        Write-Host 'Windows reported no monitors.' -ForegroundColor Red
    } elseif ($PSBoundParameters.ContainsKey('SwitchTo')) {
        Invoke-SwitchTest $monitors $edidMap $Monitor ([uint32]$SwitchTo) $WaitSeconds $From
    } else {
        Invoke-Discovery $monitors $edidMap
    }
} finally {
    [SwitchboardProbe.Ddc]::ReleaseAll()
}
