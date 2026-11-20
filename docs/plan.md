# Plan

Three stages. Each one ends with a clear "done when" check before moving on.

| Stage | Result | Estimate |
|---|---|---|
| 1. Proof of concept | We know whether and how the monitor responds to DDC/CI | 15–30 min |
| 2. Working app | Functional tray app with switching and hotkeys | A few hours |
| 3. Polished v1 | Windows 11-style design, setup wizard, installer | 1–2 days of back and forth |

Tick the boxes as work lands so any session (or Claude account) can see where things stand.

---

## Stage 1: Proof of concept

**Goal:** confirm the monitor responds to DDC/CI, and record how it behaves, before any UI work.

### Steps

- [ ] **Prep (owner):** turn on DDC/CI in the monitor's on-screen menu. Connect a second device to another input if possible.
  - Both Acers already answer. The MSI G27CQ4 has no DDC/CI option in its menu (firmware FW.013; the manual's Setting menu confirms none) and answers only some of the time (see results).
- [x] **Discovery script:** PowerShell script (`scripts/ddc-probe.ps1`) that calls `dxva2.dll` through `Add-Type` P/Invoke and, for each monitor:
  - prints the model name
  - reads the **capabilities string** (`CapabilitiesRequestAndCapabilitiesReply`) and parses the `60(...)` list of supported inputs
  - reads the **current input** (`GetVCPFeatureAndVCPFeatureReply`, code `0x60`)
  - Run: `powershell -ExecutionPolicy Bypass -File .\scripts\ddc-probe.ps1` (read-only; nothing on screen changes)
- [ ] **Switch test:** switch to another input with `SetVCPFeature`, wait 5 seconds, try to switch back. Run it twice:
  - with a device active on the other input
  - with nothing connected to it
  - Built into the same script: `-Monitor <n> -SwitchTo <value>` (e.g. `-Monitor 2 -SwitchTo 0x0F`). It asks whether a device is on the other input, switches, watches what the monitor reports, switches back (up to 3 tries) and prints a summary. Add `-WhatIf` for a dry run. **Needs the owner at the monitor.**
  - Run 1 on the MSI (HDMI 1 empty): the monitor didn't switch at all. See results.
  - Added `-DimTest` (dim for 4 s, then restore) to check whether a monitor acts on DDC/CI commands at all. On the MSI over DisplayPort: no change. Next is the cable swap below the results table.
- [ ] **Record results** in the table below. Discovery results are in; switch-test rows still open.

### Results

Discovery run on 2026-11-20 on the owner's PC, which has three monitors.

| Check | Result | Why it matters |
|---|---|---|
| Monitor model | 1: **MSI G27CQ4** (primary, EDID `MSI 3CB0`, firmware FW.013, over DisplayPort from an RTX 4070)<br>2: **Acer VG240Y** (EDID `ACR 0673`, over HDMI)<br>3: **Acer VG240Y** (EDID `ACR 06F0`, Windows reports DVI; capabilities string only says `model(acer)`) | |
| Responds to DDC/CI at all? | Acers: **yes**, every time.<br>MSI: **only some of the time.** It has stretches where every read works (8 of 8) and stretches where none do (0 of 48, input, brightness, contrast and power alike), with `0xC0262589` (invalid command field) or `0xC026258B` (bad checksum). Longer pauses between commands didn't help. Good reads return correct values (input `0F`, brightness `64` = 100). No DDC/CI option in its menu and no USB port. | Go/no-go for the whole project |
| Inputs listed in capabilities string | Acer `0673`: `01` VGA 1, `0F` DisplayPort 1, `11` HDMI 1. The monitor has no DisplayPort port, so `0F` is listed but doesn't exist.<br>Acer `06F0`: `01` VGA 1, `11` HDMI 1, `12` HDMI 2<br>MSI: capabilities string never came through. Manual lists DisplayPort, HDMI 1, HDMI 2; DisplayPort reads as `0F`. HDMI values probably `11` / `12` (untested). | Automatic input detection |
| Standard or vendor-specific input values? | Acers: **standard MCCS**. Current-input reads (`11`, `12`) match the listed values. Acer `0673` and the MSI report a maximum of `03` for VCP 60, so the maximum isn't a usable range.<br>MSI: `0F` for DisplayPort is standard. | Whether input names can be detected automatically |
| Switch-back works (other device active)? | _tbd_ (owner runs `-SwitchTo`) | Whether a workaround is needed |
| Switch-back works (other input empty)? | MSI, run 1 (2026-11-20): **the monitor didn't switch away at all.** Windows accepted the switch to `11` (HDMI 1, nothing connected) in 52 ms with no error, but the owner saw no black screen and no "No signal". 0 of 16 reads answered during the test. Either commands don't reach the MSI over DisplayPort, or it ignores a switch to an input with no signal. The brightness test (`-DimTest`) tells which. | Same |
| Time per switch | _tbd_ for the switch itself. Reading the current input takes about 60 ms; reading the capabilities string takes 1.8 to 2.8 s. | Loading feedback in the UI |

**Decision (2026-11-20):** Switchboard is for the **MSI G27CQ4** (monitor 1 in the probe).

**Brightness test on the MSI (2026-11-20):** no visible change. Windows accepted the command in 55 ms; the read-back got no answer. Together with switch run 1: **over DisplayPort the MSI doesn't act on DDC/CI commands at all, even though Windows reports success.**

**Re-check with repeated commands (2026-11-20):** `-DimTest` now resends the dim command every half second for 4 seconds, in case a single command hit a bad stretch. On the MSI: 7 sent, all 7 accepted by Windows, no visible change, no read-back. So over DisplayPort the MSI ignores commands entirely, not just some of the time.

**Next (owner): MSI over HDMI.** The owner says the Acer has no DisplayPort input, so swapping the MSI's DisplayPort cable onto an Acer is out. Instead:

1. Unplug the DisplayPort cable from the MSI.
2. Take the HDMI cable that comes straight from the graphics card to Acer `0673` (monitor 2) and plug it into the MSI's HDMI 1. That Acer has no picture during the test.
3. Run discovery (monitor numbers may change), then `-DimTest` on the MSI.

- **MSI dims over HDMI:** its DisplayPort input is the problem. Switchboard can work with the PC on HDMI and the other device on DisplayPort.

This leaves open whether DisplayPort DDC/CI works on this PC at all, since no other monitor here is on DisplayPort.

**Checked (2026-11-20):** neither Acer has a DisplayPort port, so Acer `0673`'s capabilities string lists an input (`0F`) that doesn't exist. See [ddc-ci-notes.md](ddc-ci-notes.md#known-problems).

If the MSI ignores commands over HDMI too, it doesn't support DDC/CI control in practice. Then the fallback is a workaround from [ddc-ci-notes.md](ddc-ci-notes.md#switch-back-problem), most likely an external HDMI switch the PC can control.

The script's read-back is unreliable on the MSI, so what you see on screen is the result. If the screen doesn't come back after a switch, the right side of the Navi Key opens Input Source by default.

**Done when:** we know the monitor's input values and whether switch-back works. If switch-back fails, see the workarounds in [ddc-ci-notes.md](ddc-ci-notes.md#switch-back-problem).

---

## Stage 2: Working app

**Goal:** a functional tray app usable every day. It won't be pretty yet.

### Steps

- [ ] **Project setup:** WinUI 3 app (`src/Switchboard`), tray icon only, no taskbar button, single instance.
- [ ] **Monitor service**
  - [ ] Find monitors with `EnumDisplayMonitors` → `GetPhysicalMonitorsFromHMONITOR`
  - [ ] Match each to a model name and serial from its EDID (WMI `WmiMonitorID`) so settings stay attached to the right monitor after a reboot or cable swap
  - [ ] Read and switch inputs on a background thread. DDC/CI is slow (often 50–200 ms per command), so queue commands per monitor, with retries and timeouts
  - [ ] Refresh the monitor list when displays are connected or disconnected (`WM_DISPLAYCHANGE`)
- [ ] **Settings store:** JSON file in `%LOCALAPPDATA%\Switchboard\settings.json` for monitor nicknames, input names, hidden inputs, hotkeys and custom input values.
- [ ] **Tray flyout:** list of monitors with their inputs. Click an input to switch; the current input is highlighted.
- [ ] **Hotkeys:** global shortcut per input, plus a "cycle inputs" shortcut (`RegisterHotKey`).
- [ ] **Error handling:** plain messages such as "Your monitor didn't respond. Check that DDC/CI is on." Never show raw exception text.

**Done when:** you can switch inputs from the tray and with hotkeys, and names and hotkeys survive a restart.

---

## Stage 3: Polished v1

**Goal:** something that feels like part of Windows 11.

### Steps

- [ ] **Visual design**
  - [ ] Mica background, Fluent icons, Windows accent color
  - [ ] Light and dark mode follow Windows
  - [ ] Subtle animation when the highlight moves to the new input; spinner while the monitor responds
  - [ ] Spacing and corners that match the Windows 11 Quick Settings panel
- [ ] **Automatic input detection** from the capabilities string, with friendly names ("HDMI 1", "DisplayPort"). Manual "Add input" (enter value + Test button) for monitors with non-standard values.
- [ ] **First-run setup** (four short screens):
  1. Monitors found
  2. DDC/CI test
  3. Name inputs and pick an icon for each (PC, laptop, console)
  4. Choose hotkeys
- [ ] **Settings window**
  - [ ] General: start with Windows, theme, flyout position
  - [ ] Monitors: rename, reorder, icons, hide inputs, custom values
  - [ ] Hotkeys: shortcut recorder that warns about conflicts
  - [ ] About: version and diagnostics (copy capabilities string)
- [ ] **Accessibility:** full keyboard navigation, Narrator labels, correct at any display scaling.
- [ ] **Packaging:** Velopack or Inno Setup installer, start with Windows, optional auto-update. Unsigned builds trigger a SmartScreen warning on first run; that's expected for a personal tool.
- [ ] **Testing checklist**
  - [ ] Sleep and wake
  - [ ] Unplug and replug a monitor
  - [ ] Two monitors
  - [ ] Non-100% display scaling
  - [ ] Hotkey for the input that's already showing

**Done when:** you'd be happy for a friend to install it.

---

## Later (stage 4 ideas)

Each roughly half a day to a day:

- Auto-switch when a USB device connects or disconnects (KVM-style)
- Profiles (switch several monitors at once)
- Control an external HDMI switch (USB / RS-232 / IR / network) for monitors that can't switch back
- Arduino or IR-blaster support as a last-resort workaround
