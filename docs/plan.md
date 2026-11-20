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
  - Both Acers already answer. The MSI G27CQ4 doesn't yet (see results): check its menu for a DDC/CI option, then re-run discovery.
- [x] **Discovery script:** PowerShell script (`scripts/ddc-probe.ps1`) that calls `dxva2.dll` through `Add-Type` P/Invoke and, for each monitor:
  - prints the model name
  - reads the **capabilities string** (`CapabilitiesRequestAndCapabilitiesReply`) and parses the `60(...)` list of supported inputs
  - reads the **current input** (`GetVCPFeatureAndVCPFeatureReply`, code `0x60`)
  - Run: `powershell -ExecutionPolicy Bypass -File .\scripts\ddc-probe.ps1` (read-only; nothing on screen changes)
- [ ] **Switch test:** switch to another input with `SetVCPFeature`, wait 5 seconds, try to switch back. Run it twice:
  - with a device active on the other input
  - with nothing connected to it
  - Built into the same script: `-Monitor <n> -SwitchTo <value>` (e.g. `-Monitor 2 -SwitchTo 0x0F`). It asks whether a device is on the other input, switches, watches what the monitor reports, switches back (up to 3 tries) and prints a summary. Add `-WhatIf` for a dry run. **Needs the owner at the monitor.**
- [ ] **Record results** in the table below. Discovery results are in; switch-test rows still open.

### Results

Discovery run on 2026-11-20 on the owner's PC, which has three monitors.

| Check | Result | Why it matters |
|---|---|---|
| Monitor model | 1: **MSI G27CQ4** (primary, EDID `MSI 3CB0`)<br>2: **Acer VG240Y** (EDID `ACR 0673`)<br>3: **Acer VG240Y** (EDID `ACR 06F0`; capabilities string only says `model(acer)`) | |
| Responds to DDC/CI at all? | Acers: **yes**.<br>MSI: **no**. 48 of 48 reads failed (input, brightness, contrast, power), all with `0xC0262589` (invalid command field); the capabilities request fails with `0xC026258B` (bad checksum). Likely DDC/CI is off in its menu, or the cable or adapter doesn't pass it through. | Go/no-go for the whole project |
| Inputs listed in capabilities string | Acer `0673`: `01` VGA 1, `0F` DisplayPort 1, `11` HDMI 1<br>Acer `06F0`: `01` VGA 1, `11` HDMI 1, `12` HDMI 2<br>MSI: unknown | Automatic input detection |
| Standard or vendor-specific input values? | Acers: **standard MCCS**. Current-input reads (`11`, `12`) match the listed values. Acer `0673` reports a maximum of `03` for VCP 60, so the maximum isn't a usable range. | Whether input names can be detected automatically |
| Switch-back works (other device active)? | _tbd_ (owner runs `-SwitchTo`) | Whether a workaround is needed |
| Switch-back works (other input empty)? | _tbd_ | Same |
| Time per switch | _tbd_ for the switch itself. Reading the current input takes about 60 ms; reading the capabilities string takes 1.8 to 2.8 s. | Loading feedback in the UI |

**Open question for the owner:** which monitor(s) should Switchboard switch, and what's on their other inputs? If it's the MSI, it has to answer DDC/CI first.

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
