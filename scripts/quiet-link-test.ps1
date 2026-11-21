<#
.SYNOPSIS
    Tests and times the "quiet link" trick: does the main monitor accept a DDC/CI input switch over DisplayPort
    while Windows has turned the screens off (no picture on the cable), and how short can the dark spell be?

.DESCRIPTION
    Background (docs/plan.md, stage 1): over DisplayPort the owner's MSI G27CQ4 acts on input commands
    only while it isn't showing DisplayPort. Switchboard gets around that by turning all screens off the way
    Windows does before sleep (SC_MONITORPOWER), sending the input command, and waking the screens.

    Each round does what the app does, with the timings given here:
      screens off, wait -OffSeconds, send the switch -Sends times (-IntervalMs between sends),
      wait -WakeMs, wake the screens.
    Then it asks whether the monitor switched and, unless -NoBack, switches it back to -BackTo with plain
    commands (5 s after the screens came back, one every 2 s until Windows reports a display change), and asks
    whether that worked. -Rounds repeats the whole thing.

    All screens go black for a few seconds per round. Don't touch the mouse or keyboard until they come back:
    any input wakes them early. Keep this PowerShell window on a monitor other than the one being switched.

    Every round's timeline (seconds after the screens-off command) is printed and appended to
    logs\quiet-link-test.log in the repo. Error codes in it: C0262589 garbled reply (the MSI's usual answer over
    DisplayPort), C026258B bad checksum, C0262582 no answer on the bus, C026258D handle gone (display change).

.PARAMETER Target
    Input value to switch the main monitor to. Default 0x11 (HDMI 1, where the owner's PS5 is since 2026-11-21).

.PARAMETER OffSeconds
    How long to wait after turning the screens off before the first send. Fractions are fine (0.5).

.PARAMETER Sends
    How many times to send the switch command.

.PARAMETER IntervalMs
    Pause between sends, in milliseconds (each send itself takes about 50 ms).

.PARAMETER WakeMs
    How long to wait after the last send before waking the screens, in milliseconds.

.PARAMETER Rounds
    How many times to run the test (each round switches there and back).

.PARAMETER BackTo
    Input to switch back to after each round. Default 0x0F (DisplayPort 1, this PC).

.PARAMETER NoBack
    Don't switch back (only with -Rounds 1).

.PARAMETER Watch
    Also read the input over and over while waiting, to see how the monitor's replies change after the screens
    go off. Adds DDC/CI traffic the app doesn't make, so leave it off when timing. (The only -Watch rounds so far,
    2026-11-21 12:40, came during a spell when the MSI ignored every command, so they show nothing about timing.)

.PARAMETER Delay
    Countdown before each round, in seconds.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\quiet-link-test.ps1 -OffSeconds 1 -Rounds 3
#>
param(
    [uint32]$Target = 0x11,
    [double]$OffSeconds = 3,
    [int]$Sends = 6,
    [int]$IntervalMs = 500,
    [int]$WakeMs = 1500,
    [int]$Rounds = 1,
    [uint32]$BackTo = 0x0F,
    [switch]$NoBack,
    [switch]$Watch,
    [int]$Delay = 5
)

$ErrorActionPreference = 'Stop'
if ($NoBack -and $Rounds -gt 1) { throw "-NoBack only works with -Rounds 1: the next round has to start from the PC." }
Add-Type -AssemblyName System.Windows.Forms

if (-not ('QuietLinkV2' -as [type])) {
Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public static class QuietLinkV2
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
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly List<double> changes = new List<double>();
    static Watcher watcher;

    // Seconds since the script started.
    public static double Now() { return clock.Elapsed.TotalSeconds; }

    // Starts a hidden top-level window on its own thread. It records WM_DISPLAYCHANGE and handles
    // SC_MONITORPOWER, like the app's HostWindow.
    public static void StartWatcher()
    {
        if (watcher != null) return;
        var ready = new ManualResetEvent(false);
        var thread = new Thread(() =>
        {
            watcher = new Watcher();
            watcher.CreateHandle(new CreateParams());
            ready.Set();
            Application.Run();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.WaitOne();
    }

    // When Windows reported display changes, in seconds since the script started.
    public static double[] DisplayChanges() { lock (changes) return changes.ToArray(); }

    // WM_SYSCOMMAND / SC_MONITORPOWER / 2 = off, posted like the app does.
    public static void ScreensOff() { PostMessage(watcher.Handle, 0x0112, (IntPtr)0xF170, (IntPtr)2); }

    // Any input wakes the screens: nudge the mouse by one pixel and back.
    public static void Wake()
    {
        mouse_event(0x0001, 1, 0, 0, UIntPtr.Zero);
        mouse_event(0x0001, -1, 0, 0, UIntPtr.Zero);
    }

    sealed class Watcher : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x007E) { lock (changes) changes.Add(Now()); }
            base.WndProc(ref m);
        }
    }

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
        return SetVCPFeature(h, 0x60, value) ? "accepted" : "error " + Marshal.GetLastWin32Error().ToString("X8");
    }

    public static string Read(IntPtr h)
    {
        uint cur, max;
        return GetVCPFeatureAndVCPFeatureReply(h, 0x60, IntPtr.Zero, out cur, out max)
            ? "0x" + cur.ToString("X2")
            : "error " + Marshal.GetLastWin32Error().ToString("X8");
    }

    public static void Release(IntPtr h) { DestroyPhysicalMonitor(h); }
}
'@
}

$logDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'logs'
$null = New-Item -ItemType Directory -Force -Path $logDir
$logFile = Join-Path $logDir 'quiet-link-test.log'

function Out-Log([string]$text, [string]$color) {
    if ($color) { Write-Host $text -ForegroundColor $color } else { Write-Host $text }
    Add-Content -Path $logFile -Value $text -Encoding UTF8
}

$script:handle = [IntPtr]::Zero
$script:hasHandle = $false
$script:t0 = 0.0
$events = New-Object System.Collections.Generic.List[object]

# Opens (or reopens) the main display. A display change makes the old handle useless.
function Open-Main {
    if ($script:hasHandle) { [QuietLinkV2]::Release($script:handle); $script:hasHandle = $false }
    for ($i = 0; $i -lt 10; $i++) {
        $h = [IntPtr]::Zero
        $d = $null
        if ([QuietLinkV2]::TryGetPrimary([ref]$h, [ref]$d)) {
            $script:handle = $h
            $script:hasHandle = $true
            return $d
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Couldn't open the main display for DDC/CI."
}

# Seconds since this round's screens-off command.
function T { [QuietLinkV2]::Now() - $script:t0 }

function Add-Event([string]$text, [double]$at = (T)) {
    $events.Add([pscustomobject]@{ T = $at; Last = $at; Count = 1; Text = $text; Read = $false })
}

# Repeated identical reads are kept as one line with a count.
function Add-Read {
    $at = T
    $r = [QuietLinkV2]::Read($script:handle)
    $last = if ($events.Count) { $events[$events.Count - 1] } else { $null }
    if ($last -and $last.Read -and $last.Text -eq $r) { $last.Last = $at; $last.Count++; return }
    $events.Add([pscustomobject]@{ T = $at; Last = $at; Count = 1; Text = $r; Read = $true })
}

function Wait-Until([double]$at) {
    $ms = [int](($at - (T)) * 1000)
    if ($ms -gt 0) { Start-Sleep -Milliseconds $ms }
}

# Waits until $at; with -Watch, reads the input over and over meanwhile.
function Wait-Phase([double]$at) {
    if ($Watch) {
        while ((T) + 0.12 -lt $at) { Add-Read; Start-Sleep -Milliseconds 30 }
    }
    Wait-Until $at
}

function Ask([string]$question) {
    while ($true) {
        $a = "$(Read-Host $question)".Trim().ToLowerInvariant()
        if ($a -eq 'y' -or $a -eq 'yes') { return $true }
        if ($a -eq 'n' -or $a -eq 'no') { return $false }
    }
}

function Write-Timeline {
    foreach ($c in [QuietLinkV2]::DisplayChanges()) {
        if ($c -ge $script:t0 - 0.5) { Add-Event 'Windows: display change' ($c - $script:t0) }
    }
    foreach ($e in ($events | Sort-Object T)) {
        if ($e.Read -and $e.Count -gt 1) {
            Out-Log ("  {0,6:0.00} to {1,6:0.00}  read: {2} ({3} times)" -f $e.T, $e.Last, $e.Text, $e.Count)
        } elseif ($e.Read) {
            Out-Log ("  {0,6:0.00}            read: {1}" -f $e.T, $e.Text)
        } else {
            Out-Log ("  {0,6:0.00}            {1}" -f $e.T, $e.Text)
        }
    }
}

$settingsLine = "target 0x{0:X2}, off {1} s, {2} sends {3} ms apart, wake {4} ms after the last{5}" -f `
    $Target, $OffSeconds, $Sends, $IntervalMs, $WakeMs, $(if ($Watch) { ', watching reads' } else { '' })
Out-Log ""
Out-Log ("==== {0}  quiet-link-test: {1}, {2} round(s)" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $settingsLine, $Rounds)
[QuietLinkV2]::StartWatcher()
$summary = @()

for ($round = 1; $round -le $Rounds; $round++) {
    $events.Clear()
    $desc = Open-Main
    $before = [QuietLinkV2]::Read($script:handle)
    Write-Host ""
    Write-Host ("Round {0} of {1}. Main display: {2}, input reads {3}." -f $round, $Rounds, $desc, $before)
    Write-Host ("Screens go black in {0} s. Don't touch the mouse or keyboard until they come back." -f $Delay) -ForegroundColor Yellow
    Start-Sleep -Seconds $Delay

    # ---- There: screens off, send, wake ----
    $script:t0 = [QuietLinkV2]::Now()
    [QuietLinkV2]::ScreensOff()
    Add-Event 'screens off'
    Wait-Phase $OffSeconds
    $accepted = 0
    for ($i = 1; $i -le $Sends; $i++) {
        if ($i -gt 1) { Wait-Phase ((T) + $IntervalMs / 1000) }
        $at = T
        $r = [QuietLinkV2]::Set($script:handle, $Target)
        if ($r -eq 'accepted') { $accepted++ }
        Add-Event ("send 0x{0:X2} #{1}: {2}" -f $Target, $i, $r) $at
    }
    Wait-Phase ((T) + $WakeMs / 1000)
    $woke = T
    [QuietLinkV2]::Wake()
    Add-Event 'screens woken' $woke

    # Reads after waking show whether Windows still sees the old link (the app re-finds monitors 1.5 s after).
    if ($Watch) {
        Wait-Phase ($woke + 3)
    } else {
        Wait-Until ($woke + 1)
        for ($i = 0; $i -lt 3; $i++) { Add-Read; Start-Sleep -Milliseconds 500 }
    }

    $switched = Ask ("Did the main monitor switch to 0x{0:X2} (the PS5)? (y/n)" -f $Target)
    Add-Event ("owner: {0}" -f $(if ($switched) { 'switched' } else { 'did not switch' }))

    # ---- Back: plain commands, as the app does after a screens-off switch ----
    $back = '-'
    if ($switched -and -not $NoBack) {
        # The MSI ignored plain commands 1.0 to 1.7 s after the screens came back and took one at 4.4 s.
        Wait-Until ($woke + 5)
        $null = Open-Main
        $seen = ([QuietLinkV2]::DisplayChanges()).Count
        $firstBack = T
        $changeAt = $null
        for ($k = 1; $k -le 10 -and $null -eq $changeAt; $k++) {
            $at = T
            $r = [QuietLinkV2]::Set($script:handle, $BackTo)
            Add-Event ("send 0x{0:X2} (back) #{1}: {2}" -f $BackTo, $k, $r) $at
            if ($r -like '*C026258D') { $null = Open-Main }
            $until = (T) + 2
            while ((T) -lt $until) {
                $c = [QuietLinkV2]::DisplayChanges()
                if ($c.Count -gt $seen) { $changeAt = $c[$seen] - $script:t0; break }
                Start-Sleep -Milliseconds 100
            }
        }
        $isBack = Ask ("Is the PC back on the main monitor (0x{0:X2})? (y/n)" -f $BackTo)
        if ($isBack) {
            $back = if ($null -ne $changeAt) { "yes, display change {0:0.0} s after the first send" -f ($changeAt - $firstBack) } else { 'yes, no display change seen' }
        } else {
            $back = 'NO'
            Write-Host "Switch it back to the PC with the monitor's own button (Input Source), then press Enter." -ForegroundColor Yellow
            $null = Read-Host
        }
        Add-Event ("owner: back {0}" -f $(if ($isBack) { 'yes' } else { 'no, switched by hand' }))
    }

    $result = "Round {0}: {1}. Sends accepted {2} of {3}. Dark from 0 to {4:0.00} s. Switched: {5}. Back: {6}." -f `
        $round, $settingsLine, $accepted, $Sends, $woke, $(if ($switched) { 'yes' } else { 'NO' }), $back
    Out-Log $result $(if ($switched) { 'Green' } else { 'Red' })
    Write-Timeline
    $summary += [pscustomobject]@{ Round = $round; Switched = $switched; Dark = $woke; Back = $back }
    if ($round -lt $Rounds) { Start-Sleep -Seconds 5 }
}

$ok = @($summary | Where-Object Switched).Count
Out-Log ("==== {0}: switched {1} of {2} rounds, dark for {3:0.0} s each" -f $settingsLine, $ok, $Rounds, ($summary | Measure-Object Dark -Average).Average) $(if ($ok -eq $Rounds) { 'Green' } else { 'Red' })
Out-Log ("Log: {0}" -f $logFile)
if ($script:hasHandle) { [QuietLinkV2]::Release($script:handle) }
