<#
.SYNOPSIS
    DDC/CI through NVIDIA's own I2C interface (NVAPI) instead of the Windows
    DDC/CI API (dxva2). For monitors where dxva2 gets garbled replies, like
    the owner's MSI G27CQ4 over DisplayPort.

.DESCRIPTION
    Builds the DDC/CI messages itself and sends them with NvAPI_I2CWrite /
    NvAPI_I2CRead, so it can show the raw bytes the monitor sends back.
    NVIDIA graphics cards only.

    Without a test option it lists the displays NVAPI can see. With -Display
    it runs a test on that display (\\.\DISPLAYn; the numbers in the list,
    which are NOT the same as ddc-probe.ps1's monitor numbers):

      -ReadTest      read input and brightness 20 times each (read-only; the default)
      -Capabilities  read the capabilities string (read-only)
      -DimTest       dim for 4 seconds, then restore brightness
      -SwitchTo n    switch input to n, wait, switch back

    -WhatIf walks through -DimTest or -SwitchTo without sending commands.
    -ReplyDelayMs and -I2cKhz change the timing, to experiment with flaky links.

    NVAPI function IDs come from NVIDIA's nvapi_interface.h
    (github.com/NVIDIA/nvapi, MIT licence).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\nvapi-probe.ps1

    List displays.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\nvapi-probe.ps1 -Display 3 -ReadTest

    Read display 3's input and brightness 20 times each through NVAPI.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\nvapi-probe.ps1 -Display 3 -DimTest

    Dim display 3 for 4 seconds through NVAPI, then restore it.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int]$Display = 0,

    [switch]$ReadTest,
    [switch]$Capabilities,
    [switch]$DimTest,

    [ValidateRange(1, 255)]
    [int]$SwitchTo,

    [ValidateRange(1, 255)]
    [int]$From,

    [ValidateRange(1, 60)]
    [int]$WaitSeconds = 5,

    [ValidateRange(40, 1000)]
    [int]$ReplyDelayMs = 50,

    [ValidateSet('default', '10', '33', '100')]
    [string]$I2cKhz = 'default'
)

$ErrorActionPreference = 'Stop'
$script:Cmdlet = $PSCmdlet

$source = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SwitchboardNv
{
    // NV_I2C_INFO_V3 from nvapi.h. Natural x64 alignment matches the C layout.
    [StructLayout(LayoutKind.Sequential)]
    public struct NV_I2C_INFO_V3
    {
        public uint version;
        public uint displayMask;
        public byte bIsDDCPort;
        public byte i2cDevAddress;      // 8-bit form: I2C address shifted left by one
        public IntPtr pbI2cRegAddress;
        public uint regAddrSize;
        public IntPtr pbData;
        public uint cbSize;
        public uint i2cSpeed;           // deprecated; 0xFFFF
        public int i2cSpeedKhz;         // NV_I2C_SPEED
        public byte portId;
        public uint bIsPortIdSet;
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

    public class GdiDisplay
    {
        public int Number;              // n in \\.\DISPLAYn
        public string GdiName;
        public string DeviceInterface;  // to match the EDID in WMI
    }

    public class I2cResult
    {
        public int Status;              // NvAPI_Status; 0 = OK
        public string Message;
        public byte[] Data;             // bytes read (reads only)
    }

    public class VcpReply
    {
        public bool Ok;
        public uint Current;
        public uint Maximum;
        public string Problem;
        public string Raw;              // hex of the reply bytes, when there was one
        public long Ms;
    }

    public static class Gdi
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE info, uint flags);

        public static List<GdiDisplay> List()
        {
            var result = new List<GdiDisplay>();
            for (uint i = 0; i < 32; i++)
            {
                var adapter = new DISPLAY_DEVICE();
                adapter.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
                if ((adapter.StateFlags & 1) == 0) continue; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP

                var d = new GdiDisplay();
                d.GdiName = adapter.DeviceName;
                int n;
                string digits = adapter.DeviceName.Replace(@"\\.\DISPLAY", "");
                d.Number = int.TryParse(digits, out n) ? n : 0;

                var monitor = new DISPLAY_DEVICE();
                monitor.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 1)) // EDD_GET_DEVICE_INTERFACE_NAME
                {
                    d.DeviceInterface = monitor.DeviceID;
                }
                result.Add(d);
            }
            return result;
        }
    }

    public static class Nv
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgsFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetErrorMessageFn(int status, IntPtr shortString);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDisplayIdByDisplayNameFn([MarshalAs(UnmanagedType.LPStr)] string name, out uint displayId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPhysicalGpuFromDisplayIdFn(uint displayId, out IntPtr gpu);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int I2cFn(IntPtr gpu, ref NV_I2C_INFO_V3 info);

        // IDs from NVIDIA's nvapi_interface.h
        private const uint IdInitialize = 0x0150E828;
        private const uint IdUnload = 0xD22BDD7E;
        private const uint IdGetErrorMessage = 0x6C2D048C;
        private const uint IdGetDisplayIdByDisplayName = 0xAE457190;
        private const uint IdGetPhysicalGpuFromDisplayId = 0x9EA74659;
        private const uint IdI2CRead = 0x2FDE12C5;
        private const uint IdI2CWrite = 0xE812EB07;

        private const byte DdcAddress = 0x6E;      // DDC/CI device 0x37, 8-bit form
        private const byte HostAddress = 0x51;     // source address of host-to-monitor messages

        private static bool initialized;
        private static GetErrorMessageFn getErrorMessage;
        private static GetDisplayIdByDisplayNameFn getDisplayId;
        private static GetPhysicalGpuFromDisplayIdFn getGpu;
        private static I2cFn i2cRead;
        private static I2cFn i2cWrite;

        private static T Get<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            if (p == IntPtr.Zero) throw new InvalidOperationException("This NVIDIA driver doesn't provide NVAPI function 0x" + id.ToString("X8") + ".");
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        // Returns null when NVAPI is ready, otherwise what went wrong.
        public static string Init()
        {
            if (initialized) return null;
            try
            {
                int s = Get<NoArgsFn>(IdInitialize)();
                if (s != 0) return "NVAPI didn't start (status " + s + "). Is an NVIDIA graphics driver installed?";
                getErrorMessage = Get<GetErrorMessageFn>(IdGetErrorMessage);
                getDisplayId = Get<GetDisplayIdByDisplayNameFn>(IdGetDisplayIdByDisplayName);
                getGpu = Get<GetPhysicalGpuFromDisplayIdFn>(IdGetPhysicalGpuFromDisplayId);
                i2cRead = Get<I2cFn>(IdI2CRead);
                i2cWrite = Get<I2cFn>(IdI2CWrite);
                initialized = true;
                return null;
            }
            catch (DllNotFoundException)
            {
                return "nvapi64.dll wasn't found. This script needs an NVIDIA graphics card and driver.";
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        public static void Shutdown()
        {
            if (!initialized) return;
            try { Get<NoArgsFn>(IdUnload)(); } catch { }
            initialized = false;
        }

        public static string StatusText(int status)
        {
            if (status == 0) return "OK";
            string text = null;
            if (getErrorMessage != null)
            {
                IntPtr buffer = Marshal.AllocHGlobal(64);
                try
                {
                    for (int i = 0; i < 64; i++) Marshal.WriteByte(buffer, i, 0);
                    if (getErrorMessage(status, buffer) == 0) text = Marshal.PtrToStringAnsi(buffer);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return (string.IsNullOrEmpty(text) ? "NVAPI error" : text) + " (" + status + ")";
        }

        // NVAPI display ID and GPU for a GDI name such as \\.\DISPLAY3. Returns an NvAPI_Status.
        public static int Resolve(string gdiName, out uint displayId, out IntPtr gpu)
        {
            gpu = IntPtr.Zero;
            int s = getDisplayId(gdiName, out displayId);
            if (s != 0) return s;
            return getGpu(displayId, out gpu);
        }

        private static I2cResult Transfer(bool write, IntPtr gpu, uint displayId, byte[] register, byte[] data, int readLength, int speed)
        {
            var info = new NV_I2C_INFO_V3();
            info.version = (uint)Marshal.SizeOf(typeof(NV_I2C_INFO_V3)) | (3u << 16); // MAKE_NVAPI_VERSION(NV_I2C_INFO_V3, 3)
            info.displayMask = displayId;
            info.bIsDDCPort = 1;
            info.i2cDevAddress = DdcAddress;
            info.i2cSpeed = 0xFFFF;   // NVAPI_I2C_SPEED_DEPRECATED
            info.i2cSpeedKhz = speed;

            int length = write ? data.Length : readLength;
            IntPtr registerPtr = IntPtr.Zero;
            IntPtr dataPtr = IntPtr.Zero;
            try
            {
                if (register != null && register.Length > 0)
                {
                    registerPtr = Marshal.AllocHGlobal(register.Length);
                    Marshal.Copy(register, 0, registerPtr, register.Length);
                    info.pbI2cRegAddress = registerPtr;
                    info.regAddrSize = (uint)register.Length;
                }
                dataPtr = Marshal.AllocHGlobal(Math.Max(length, 1));
                if (write) Marshal.Copy(data, 0, dataPtr, data.Length);
                else for (int i = 0; i < length; i++) Marshal.WriteByte(dataPtr, i, 0);
                info.pbData = dataPtr;
                info.cbSize = (uint)length;

                var r = new I2cResult();
                r.Status = write ? i2cWrite(gpu, ref info) : i2cRead(gpu, ref info);
                r.Message = StatusText(r.Status);
                if (!write && r.Status == 0)
                {
                    r.Data = new byte[length];
                    Marshal.Copy(dataPtr, r.Data, 0, length);
                }
                return r;
            }
            finally
            {
                if (registerPtr != IntPtr.Zero) Marshal.FreeHGlobal(registerPtr);
                if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
            }
        }

        // DDC/CI host-to-monitor message: 51 | 80+len | payload | checksum (XOR of 6E, 51 and everything after).
        private static I2cResult SendMessage(IntPtr gpu, uint displayId, byte[] payload, int speed)
        {
            var data = new byte[payload.Length + 2];
            data[0] = (byte)(0x80 | payload.Length);
            Array.Copy(payload, 0, data, 1, payload.Length);
            byte check = DdcAddress ^ HostAddress;
            for (int i = 0; i < data.Length - 1; i++) check ^= data[i];
            data[data.Length - 1] = check;
            return Transfer(true, gpu, displayId, new byte[] { HostAddress }, data, 0, speed);
        }

        private static I2cResult ReadReply(IntPtr gpu, uint displayId, int length, int speed)
        {
            return Transfer(false, gpu, displayId, null, null, length, speed);
        }

        // Monitor-to-host checksum: XOR of 50 and every byte from the 6E source byte on.
        private static bool ChecksumOk(byte[] b, int count, out byte expected)
        {
            expected = 0x50;
            for (int i = 0; i < count; i++) expected ^= b[i];
            return expected == b[count];
        }

        public static VcpReply GetVcp(IntPtr gpu, uint displayId, byte code, int delayMs, int speed)
        {
            var r = new VcpReply();
            var clock = Stopwatch.StartNew();
            var w = SendMessage(gpu, displayId, new byte[] { 0x01, code }, speed);
            if (w.Status != 0)
            {
                r.Problem = "request not sent: " + w.Message;
                r.Ms = clock.ElapsedMilliseconds;
                return r;
            }
            Thread.Sleep(delayMs);
            var rd = ReadReply(gpu, displayId, 11, speed);
            r.Ms = clock.ElapsedMilliseconds;
            if (rd.Status != 0)
            {
                r.Problem = "reply not read: " + rd.Message;
                return r;
            }
            r.Raw = Hex(rd.Data, rd.Data.Length);
            byte[] b = rd.Data;
            byte expected;

            // Expected reply: 6E 88 02 rc vcp type maxH maxL curH curL checksum
            if (b[0] == 0x6E && b[1] == 0x80) { r.Problem = "null reply (monitor had nothing to send)"; return r; }
            if (b[0] != 0x6E) { r.Problem = "reply doesn't start with 6E"; return r; }
            if (b[1] != 0x88) { r.Problem = "unexpected length byte " + b[1].ToString("X2"); return r; }
            if (!ChecksumOk(b, 10, out expected)) { r.Problem = "bad checksum"; return r; }
            if (b[2] != 0x02) { r.Problem = "wrong reply type " + b[2].ToString("X2"); return r; }
            if (b[3] == 0x01) { r.Problem = "monitor says VCP code not supported"; return r; }
            if (b[3] != 0x00) { r.Problem = "result code " + b[3].ToString("X2"); return r; }
            if (b[4] != code) { r.Problem = "reply is for VCP " + b[4].ToString("X2"); return r; }
            r.Maximum = (uint)((b[6] << 8) | b[7]);
            r.Current = (uint)((b[8] << 8) | b[9]);
            r.Ok = true;
            return r;
        }

        public static I2cResult SetVcp(IntPtr gpu, uint displayId, byte code, uint value, int speed)
        {
            return SendMessage(gpu, displayId, new byte[] { 0x03, code, (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF) }, speed);
        }

        // Reads the capabilities string fragment by fragment (up to 32 bytes each).
        public static string ReadCapabilities(IntPtr gpu, uint displayId, int delayMs, int speed, out string problem, out int fragments, out int retries)
        {
            problem = null;
            fragments = 0;
            retries = 0;
            var text = new StringBuilder();
            int offset = 0;
            while (offset < 8192)
            {
                byte[] fragment = null;
                string lastProblem = null;
                for (int attempt = 0; attempt < 5 && fragment == null; attempt++)
                {
                    if (attempt > 0) { retries++; Thread.Sleep(100); }
                    var w = SendMessage(gpu, displayId, new byte[] { 0xF3, (byte)(offset >> 8), (byte)(offset & 0xFF) }, speed);
                    if (w.Status != 0) { lastProblem = "request not sent: " + w.Message; continue; }
                    Thread.Sleep(delayMs);
                    var rd = ReadReply(gpu, displayId, 38, speed);
                    if (rd.Status != 0) { lastProblem = "reply not read: " + rd.Message; continue; }
                    fragment = ParseCapsFragment(rd.Data, offset, out lastProblem);
                }
                if (fragment == null)
                {
                    problem = "stopped at byte " + offset + ": " + lastProblem;
                    return text.Length > 0 ? text.ToString() : null;
                }
                fragments++;
                if (fragment.Length == 0) break;
                int end = Array.IndexOf(fragment, (byte)0);
                text.Append(Encoding.ASCII.GetString(fragment, 0, end >= 0 ? end : fragment.Length));
                if (end >= 0) break;
                offset += fragment.Length;
                Thread.Sleep(50);
            }
            return text.ToString();
        }

        // Expected: 6E 80+len E3 offH offL data... checksum, where len = 3 + data length.
        private static byte[] ParseCapsFragment(byte[] b, int offset, out string problem)
        {
            problem = null;
            byte expected;
            if (b[0] != 0x6E) { problem = "reply doesn't start with 6E: " + Hex(b, 8); return null; }
            if (b[1] == 0x80) { problem = "null reply"; return null; }
            int len = b[1] & 0x7F;
            if ((b[1] & 0x80) == 0 || len < 3 || len + 3 > b.Length) { problem = "unexpected length byte " + b[1].ToString("X2"); return null; }
            if (!ChecksumOk(b, len + 2, out expected)) { problem = "bad checksum: " + Hex(b, len + 3); return null; }
            if (b[2] != 0xE3) { problem = "wrong reply type " + b[2].ToString("X2"); return null; }
            int replyOffset = (b[3] << 8) | b[4];
            if (replyOffset != offset) { problem = "reply is for byte " + replyOffset; return null; }
            var data = new byte[len - 3];
            Array.Copy(b, 5, data, 0, len - 3);
            return data;
        }

        public static string Hex(byte[] b, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(count, b.Length); i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
'@

if (-not ('SwitchboardNv.Nv' -as [type])) {
    Add-Type -TypeDefinition $source -Language CSharp
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

$VcpInputSource = [byte]0x60
$VcpBrightness = [byte]0x10
$Speed = switch ($I2cKhz) { 'default' { 0 } '10' { 2 } '33' { 3 } '100' { 4 } }  # NV_I2C_SPEED

$script:InputNames = @{
    0x01 = 'VGA 1'; 0x02 = 'VGA 2'; 0x03 = 'DVI 1'; 0x04 = 'DVI 2'
    0x0F = 'DisplayPort 1'; 0x10 = 'DisplayPort 2'
    0x11 = 'HDMI 1'; 0x12 = 'HDMI 2'; 0x1B = 'USB-C'
}

function Format-HexValue([uint32]$Value) {
    if ($Value -gt 0xFF) { '0x{0:X4}' -f $Value } else { '0x{0:X2}' -f $Value }
}

function Format-Input([uint32]$Value) {
    $name = $script:InputNames[[int]($Value -band 0xFF)]
    if ($name) { '{0} ({1})' -f (Format-HexValue $Value), $name } else { '{0} (not a standard value)' -f (Format-HexValue $Value) }
}

function Convert-EdidString($Codes) {
    if (-not $Codes) { return '' }
    (-join ($Codes | Where-Object { $_ -ne 0 } | ForEach-Object { [char][int]$_ })).Trim()
}

# Monitor name and cable type per WMI instance name, as in ddc-probe.ps1.
function Get-WmiMonitors {
    $WhatIfPreference = $false
    $map = @{}
    try {
        foreach ($m in Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction Stop) {
            $map[$m.InstanceName.ToUpperInvariant()] = [pscustomobject]@{ Name = Convert-EdidString $m.UserFriendlyName; Connection = $null }
        }
        foreach ($c in Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorConnectionParams -ErrorAction Stop) {
            $entry = $map[$c.InstanceName.ToUpperInvariant()]
            if (-not $entry) { continue }
            $entry.Connection = switch ([int64]$c.VideoOutputTechnology) {
                0 { 'VGA' } 4 { 'DVI' } 5 { 'HDMI' } 10 { 'DisplayPort' } 11 { 'DisplayPort (embedded)' }
                2147483648 { 'built-in panel' } default { 'type {0}' -f $_ }
            }
        }
    } catch {
    }
    $map
}

function Find-WmiMonitor($Map, [string]$DeviceInterface) {
    if (-not $DeviceInterface) { return $null }
    $id = $DeviceInterface
    if ($id.StartsWith('\\?\')) { $id = $id.Substring(4) }
    $brace = $id.IndexOf('#{')
    if ($brace -ge 0) { $id = $id.Substring(0, $brace) }
    $id = $id.Replace('#', '\').ToUpperInvariant()
    foreach ($key in $Map.Keys) { if ($key.StartsWith($id + '_')) { return $Map[$key] } }
    $null
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

function Send-NvVcp($Target, [byte]$Code, [uint32]$Value, [string]$Action) {
    if (-not $script:Cmdlet.ShouldProcess(('Display {0}' -f $Target.Number), $Action)) {
        $skipped = New-Object SwitchboardNv.I2cResult
        $skipped.Status = 0
        $skipped.Message = 'skipped (-WhatIf)'
        return $skipped
    }
    [SwitchboardNv.Nv]::SetVcp($Target.Gpu, $Target.Id, $Code, $Value, $Speed)
}

function Read-NvVcp($Target, [byte]$Code, [int]$Tries) {
    $r = $null
    for ($i = 0; $i -lt $Tries; $i++) {
        $r = [SwitchboardNv.Nv]::GetVcp($Target.Gpu, $Target.Id, $Code, $ReplyDelayMs, $Speed)
        if ($r.Ok) { return $r }
        Start-Sleep -Milliseconds 100
    }
    $r
}

function Get-Displays {
    $wmi = Get-WmiMonitors
    foreach ($d in [SwitchboardNv.Gdi]::List()) {
        $w = Find-WmiMonitor $wmi $d.DeviceInterface
        $id = [uint32]0
        $gpu = [IntPtr]::Zero
        $status = [SwitchboardNv.Nv]::Resolve($d.GdiName, [ref]$id, [ref]$gpu)
        [pscustomobject]@{
            Number     = $d.Number
            GdiName    = $d.GdiName
            Name       = if ($w -and $w.Name) { $w.Name } else { 'Unknown monitor' }
            Connection = if ($w) { $w.Connection } else { $null }
            Id         = $id
            Gpu        = $gpu
            NvStatus   = $status
        }
    }
}

function Get-Label($Target) {
    $via = if ($Target.Connection) { ' over {0}' -f $Target.Connection } else { '' }
    'display {0}: {1}{2}' -f $Target.Number, $Target.Name, $via
}

# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

function Invoke-NvReadTest($Target) {
    $reads = 20
    Write-Host ('NVAPI read test on {0} (read-only, {1} reads per code, reply delay {2} ms, I2C speed {3})' -f (Get-Label $Target), $reads, $ReplyDelayMs, $I2cKhz)
    $totalOk = 0
    $codes = @(
        @{ Code = $VcpInputSource; Label = 'Input (VCP 60):     ' },
        @{ Code = $VcpBrightness;  Label = 'Brightness (VCP 10):' }
    )
    foreach ($c in $codes) {
        $ok = 0
        $values = New-Object System.Collections.Generic.List[string]
        $problems = @{}
        $samples = New-Object System.Collections.Generic.List[string]
        $okSample = $null
        for ($i = 0; $i -lt $reads; $i++) {
            $r = [SwitchboardNv.Nv]::GetVcp($Target.Gpu, $Target.Id, $c.Code, $ReplyDelayMs, $Speed)
            if ($r.Ok) {
                $ok++
                $v = Format-HexValue $r.Current
                if (-not $values.Contains($v)) { $values.Add($v) }
                if (-not $okSample) { $okSample = $r.Raw }
            } else {
                $problems[$r.Problem] = 1 + [int]$problems[$r.Problem]
                if ($r.Raw -and $samples.Count -lt 3 -and -not $samples.Contains($r.Raw)) { $samples.Add($r.Raw) }
            }
            Start-Sleep -Milliseconds 100
        }
        $totalOk += $ok
        $valueText = if ($values.Count -gt 0) { '; values: ' + ($values -join ', ') } else { '' }
        $color = if ($ok -eq $reads) { 'Green' } elseif ($ok -gt 0) { 'Yellow' } else { 'Red' }
        Write-Host ('  {0} {1,2} of {2} worked{3}' -f $c.Label, $ok, $reads, $valueText) -ForegroundColor $color
        foreach ($p in $problems.GetEnumerator()) { Write-Host ('      {0} x{1}' -f $p.Key, $p.Value) }
        if ($okSample) { Write-Host ('      good reply: {0}' -f $okSample) }
        foreach ($s in $samples) { Write-Host ('      bad reply:  {0}' -f $s) }
    }
    $all = $reads * $codes.Count
    if ($totalOk -eq $all) { Write-Host '  Verdict: reliable' -ForegroundColor Green }
    elseif ($totalOk -gt 0) { Write-Host ('  Verdict: unreliable ({0} of {1} reads worked)' -f $totalOk, $all) -ForegroundColor Yellow }
    else { Write-Host '  Verdict: no answers' -ForegroundColor Red }
}

function Invoke-NvCapabilities($Target) {
    Write-Host ('NVAPI capabilities read on {0} (read-only)' -f (Get-Label $Target))
    $problem = $null
    $fragments = 0
    $retries = 0
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $text = [SwitchboardNv.Nv]::ReadCapabilities($Target.Gpu, $Target.Id, $ReplyDelayMs, $Speed, [ref]$problem, [ref]$fragments, [ref]$retries)
    $ms = $clock.ElapsedMilliseconds
    if ($problem) {
        Write-Host ('  Failed after {0} ms, {1} fragment(s), {2} retries: {3}' -f $ms, $fragments, $retries, $problem) -ForegroundColor Red
        if ($text) { Write-Host ('  Partial string: {0}' -f $text) }
        return
    }
    Write-Host ('  Read in {0} ms: {1} fragment(s), {2} retries' -f $ms, $fragments, $retries) -ForegroundColor Green
    Write-Host ('  {0}' -f $text)
    if ($text -match '(?i)(?<![0-9a-f])60\s*\(([^)]*)\)') {
        $hex = ($Matches[1] -replace '[^0-9A-Fa-f]', ' ').Trim() -split '\s+' | Where-Object { $_ }
        $values = foreach ($h in $hex) { if ($h.Length -le 2) { $h } else { for ($i = 0; $i + 1 -lt $h.Length; $i += 2) { $h.Substring($i, 2) } } }
        Write-Host '  Inputs listed:'
        foreach ($v in $values) { Write-Host ('    {0}' -f (Format-Input ([Convert]::ToUInt32($v, 16)))) }
    }
}

function Invoke-NvDimTest($Target) {
    $before = Read-NvVcp $Target $VcpBrightness 10
    if ($before.Ok) { $restore = [uint32]$before.Current; $nowText = [string]$restore }
    else { $restore = [uint32]100; $nowText = 'unknown' }
    $test = if ($restore -ge 50) { [uint32]10 } else { [uint32]90 }

    Write-Host ('NVAPI brightness test on {0}' -f (Get-Label $Target))
    Write-Host ('  Brightness now:   {0}' -f $nowText)
    Write-Host ('  Test:             {0} for 4 seconds, then back to {1}' -f $test, $restore)
    if (-not $before.Ok) {
        Write-Host ("  The monitor didn't report its brightness, so it goes back to 100. If that's wrong, set it in the monitor's menu afterwards.") -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host 'Watch this monitor. Press Ctrl+C now to cancel.' -ForegroundColor Yellow
    for ($i = 3; $i -ge 1; $i--) { Write-Host ('  Starting in {0}...' -f $i); Start-Sleep -Seconds 1 }

    $sent = 0; $accepted = 0
    $errors = New-Object System.Collections.Generic.List[string]
    $readBack = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt 4000) {
        $s = Send-NvVcp $Target $VcpBrightness $test ('Set brightness to {0}' -f $test)
        $sent++
        if ($s.Status -eq 0) { $accepted++ } elseif (-not $errors.Contains($s.Message)) { $errors.Add($s.Message) }
        Start-Sleep -Milliseconds 250
        $r = [SwitchboardNv.Nv]::GetVcp($Target.Gpu, $Target.Id, $VcpBrightness, $ReplyDelayMs, $Speed)
        if ($r.Ok) { $readBack = [uint32]$r.Current }
        Start-Sleep -Milliseconds 250
    }
    for ($i = 0; $i -lt 3; $i++) {
        $s = Send-NvVcp $Target $VcpBrightness $restore ('Set brightness back to {0}' -f $restore)
        if ($s.Status -ne 0 -and -not $errors.Contains($s.Message)) { $errors.Add($s.Message) }
        Start-Sleep -Milliseconds 300
    }

    Write-Host ''
    $sawIt = Read-Answer 'Did the screen get noticeably dimmer (or brighter) for a few seconds? [y/n]'
    $isBack = Read-Answer 'Is the brightness back to normal now? [y/n]'
    $dryRun = if ($WhatIfPreference) { ' (dry run with -WhatIf: no commands were sent)' } else { '' }
    Write-Host ''
    Write-Host ('NVAPI brightness test result{0}' -f $dryRun)
    Write-Host ('  Display:                 {0}' -f (Get-Label $Target))
    Write-Host ('  Test:                    brightness {0} -> {1} -> {2}' -f $nowText, $test, $restore)
    Write-Host ('  Command:                 sent {0} times over 4 s; NVAPI accepted {1}' -f $sent, $accepted)
    if ($errors.Count -gt 0) { Write-Host ('  Command errors:          {0}' -f ($errors -join '; ')) -ForegroundColor Yellow }
    Write-Host ('  Read back during test:   {0}' -f $(if ($null -ne $readBack) { $readBack } else { 'no answer' }))
    Write-Host ('  You saw it change:       {0}' -f $(if ($sawIt) { $sawIt } else { 'not answered' }))
    Write-Host ('  Back to normal:          {0}' -f $(if ($isBack) { $isBack } else { 'not answered' }))
}

function Invoke-NvSwitchTest($Target, [uint32]$Target2, [int]$Assumed, [int]$Wait) {
    $before = Read-NvVcp $Target $VcpInputSource 10
    if ($before.Ok) {
        $original = [uint32]($before.Current -band 0xFF)
    } elseif ($Assumed -gt 0) {
        $original = [uint32]$Assumed
        Write-Host ("Couldn't read the current input, so using -From {0}." -f (Format-Input $original)) -ForegroundColor Yellow
    } else {
        Write-Host ("Couldn't read the current input ({0}). Run it again, or say which input this PC is on with -From (for example -From 0x0F)." -f $before.Problem) -ForegroundColor Red
        return
    }
    if ($original -eq $Target2) {
        Write-Host ('Already on {0}. Pick a different input.' -f (Format-Input $Target2)) -ForegroundColor Yellow
        return
    }

    Write-Host ('NVAPI switch test on {0}' -f (Get-Label $Target))
    Write-Host ('  This PC:     {0}' -f (Format-Input $original))
    Write-Host ('  Switch to:   {0}' -f (Format-Input $Target2))
    $deviceActive = Read-Answer ('Is a device connected to {0} and turned on? [y/n]' -f (Format-Input $Target2))
    Write-Host ''
    Write-Host ('The screen will switch away for about {0} seconds, then the script switches it back.' -f $Wait) -ForegroundColor Yellow
    Write-Host "If it doesn't come back, press the input button on the monitor. Press Ctrl+C now to cancel." -ForegroundColor Yellow
    for ($i = 3; $i -ge 1; $i--) { Write-Host ('  Starting in {0}...' -f $i); Start-Sleep -Seconds 1 }

    $away = Send-NvVcp $Target $VcpInputSource $Target2 ('Switch input to {0}' -f (Format-Input $Target2))
    $readsOk = 0; $readsFailed = 0
    $seen = New-Object System.Collections.Generic.List[string]
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt ($Wait * 1000)) {
        $r = [SwitchboardNv.Nv]::GetVcp($Target.Gpu, $Target.Id, $VcpInputSource, $ReplyDelayMs, $Speed)
        if ($r.Ok) { $readsOk++; $h = Format-HexValue ($r.Current -band 0xFF); if (-not $seen.Contains($h)) { $seen.Add($h) } } else { $readsFailed++ }
        Start-Sleep -Milliseconds 250
    }
    $backErrors = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt 3; $i++) {
        $s = Send-NvVcp $Target $VcpInputSource $original ('Switch input back to {0}' -f (Format-Input $original))
        if ($s.Status -ne 0 -and -not $backErrors.Contains($s.Message)) { $backErrors.Add($s.Message) }
        Start-Sleep -Milliseconds 500
    }

    Write-Host ''
    $sawSwitch = Read-Answer 'Did the screen leave this PC at any point (black, "No signal" or the other device)? [y/n]'
    $sawIt = Read-Answer 'Is this screen showing this PC again? [y/n]'
    $dryRun = if ($WhatIfPreference) { ' (dry run with -WhatIf: no switch commands were sent)' } else { '' }
    Write-Host ''
    Write-Host ('NVAPI switch test result{0}' -f $dryRun)
    Write-Host ('  Display:                 {0}' -f (Get-Label $Target))
    Write-Host ('  Test:                    {0} -> {1} -> back' -f (Format-Input $original), (Format-Input $Target2))
    Write-Host ('  Device on other input:   {0}' -f $(if ($deviceActive) { $deviceActive } else { 'not answered' }))
    Write-Host ('  Switch command:          {0}' -f $away.Message)
    $seenText = if ($seen.Count -gt 0) { '; values seen: ' + ($seen -join ', ') } else { '' }
    Write-Host ('  Reads while away:        {0} answered, {1} failed{2}' -f $readsOk, $readsFailed, $seenText)
    if ($backErrors.Count -gt 0) { Write-Host ('  Switch-back errors:      {0}' -f ($backErrors -join '; ')) -ForegroundColor Yellow }
    Write-Host ('  You saw it switch away:  {0}' -f $(if ($sawSwitch) { $sawSwitch } else { 'not answered' }))
    Write-Host ('  You saw this PC again:   {0}' -f $(if ($sawIt) { $sawIt } else { 'not answered' }))
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

$problem = [SwitchboardNv.Nv]::Init()
if ($problem) {
    Write-Host $problem -ForegroundColor Red
    return
}
try {
    $displays = @(Get-Displays)
    if ($Display -eq 0) {
        Write-Host 'Displays NVAPI can see (use the number with -Display):'
        foreach ($d in $displays) {
            $label = Get-Label $d
            $label = $label.Substring(0, 1).ToUpperInvariant() + $label.Substring(1)
            if ($d.NvStatus -eq 0) {
                Write-Host ('  {0}  [NVAPI display id 0x{1:X8}]' -f $label, $d.Id)
            } else {
                Write-Host ('  {0}  [NVAPI: {1}]' -f $label, [SwitchboardNv.Nv]::StatusText($d.NvStatus)) -ForegroundColor Yellow
            }
        }
        Write-Host ''
        Write-Host 'Next, for example: powershell -ExecutionPolicy Bypass -File .\scripts\nvapi-probe.ps1 -Display <n> -ReadTest'
        return
    }

    $target = $displays | Where-Object { $_.Number -eq $Display } | Select-Object -First 1
    if (-not $target) {
        Write-Host ("There's no display {0}. Run the script with no options to see the list." -f $Display) -ForegroundColor Red
        return
    }
    if ($target.NvStatus -ne 0) {
        Write-Host ("NVAPI can't reach display {0}: {1}" -f $Display, [SwitchboardNv.Nv]::StatusText($target.NvStatus)) -ForegroundColor Red
        return
    }

    if ($PSBoundParameters.ContainsKey('SwitchTo')) {
        Invoke-NvSwitchTest $target ([uint32]$SwitchTo) $From $WaitSeconds
    } elseif ($DimTest) {
        Invoke-NvDimTest $target
    } elseif ($Capabilities) {
        Invoke-NvCapabilities $target
    } else {
        Invoke-NvReadTest $target
    }
} finally {
    [SwitchboardNv.Nv]::Shutdown()
}
