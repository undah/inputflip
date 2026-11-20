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
  - Both Acers already answer. The MSI G27CQ4 has no DDC/CI option in its menu (firmware FW.013; the manual's Setting menu confirms none). Over DisplayPort it barely answers; **over HDMI it works** (see results).
- [x] **Discovery script:** PowerShell script (`scripts/ddc-probe.ps1`) that calls `dxva2.dll` through `Add-Type` P/Invoke and, for each monitor:
  - prints the model name
  - reads the **capabilities string** (`CapabilitiesRequestAndCapabilitiesReply`) and parses the `60(...)` list of supported inputs
  - reads the **current input** (`GetVCPFeatureAndVCPFeatureReply`, code `0x60`)
  - Run: `powershell -ExecutionPolicy Bypass -File .\scripts\ddc-probe.ps1` (read-only; nothing on screen changes)
- [ ] **Switch test:** switch to another input with `SetVCPFeature`, wait 5 seconds, try to switch back. Run it twice:
  - with a device active on the other input
  - with nothing connected to it
  - Built into the same script: `-Monitor <n> -SwitchTo <value>` (e.g. `-Monitor 2 -SwitchTo 0x0F`). It asks whether a device is on the other input, switches, watches what the monitor reports, switches back (up to 3 tries) and prints a summary. Add `-WhatIf` for a dry run. **Needs the owner at the monitor.**
  - Run 1 on the MSI over DisplayPort (HDMI 1 empty): the monitor didn't switch at all. See results.
  - Run 2 on the MSI over HDMI (DisplayPort empty): **switched away and back.** The "other input empty" case is done; "device active" is still open.
  - Added `-DimTest` (dim for 4 s, then restore) to check whether a monitor acts on DDC/CI commands at all. On the MSI over DisplayPort: no change. Over HDMI: it dimmed, and the read-back gave `10`.
- [ ] **Record results** in the table below. Discovery results are in; switch-test rows still open.

### Results

Discovery run on 2026-11-20 on the owner's PC, which has three monitors.

| Check | Result | Why it matters |
|---|---|---|
| Monitor model | 1: **MSI G27CQ4** (primary, EDID `MSI 3CB0`, firmware FW.013, over DisplayPort from an RTX 4070; moved to its HDMI 1 for testing, through the cable from Acer `06F0`, which Windows reports as DVI)<br>2: **Acer VG240Y** (EDID `ACR 0673`, over HDMI)<br>3: **Acer VG240Y** (EDID `ACR 06F0`, Windows reports DVI; capabilities string only says `model(acer)`) | |
| Responds to DDC/CI at all? | Acers: **yes**, every time.<br>MSI over HDMI: **yes.** Capabilities string and current input read on the first try; the brightness test dimmed it (read-back `10`) and restored it.<br>MSI over DisplayPort: **only some of the time, and it ignores commands.** It has stretches where every read works (8 of 8) and stretches where none do (0 of 48, input, brightness, contrast and power alike), with `0xC0262589` (invalid command field) or `0xC026258B` (bad checksum). Longer pauses between commands didn't help. Good reads return correct values (input `0F`, brightness `64` = 100). No DDC/CI option in its menu and no USB port. | Go/no-go for the whole project |
| Inputs listed in capabilities string | Acer `0673`: `01` VGA 1, `0F` DisplayPort 1, `11` HDMI 1. The monitor has no DisplayPort port, so `0F` is listed but doesn't exist.<br>Acer `06F0`: `01` VGA 1, `11` HDMI 1, `12` HDMI 2<br>MSI (read over HDMI; `model(FALCON)`, MCCS 2.0): `11` HDMI 1, `12` HDMI 2, `0F` DisplayPort 1, `10` DisplayPort 2. Its manual shows one DisplayPort port, so `10` doesn't exist. Over DisplayPort the string never came through. | Automatic input detection |
| Standard or vendor-specific input values? | Acers: **standard MCCS**. Current-input reads (`11`, `12`) match the listed values. Acer `0673` and the MSI report a maximum of `03` for VCP 60, so the maximum isn't a usable range.<br>MSI: **standard MCCS**. The current input reads `11` over HDMI 1 and `0F` over DisplayPort. | Whether input names can be detected automatically |
| Switch-back works (other device active)? | _tbd_ (owner runs `-SwitchTo`) | Whether a workaround is needed |
| Switch-back works (other input empty)? | MSI over HDMI, run 2 (2026-11-20): **yes.** Switched from `11` (HDMI 1, this PC) to `0F` (DisplayPort, nothing connected) and back; the owner saw it leave and come back. The MSI kept answering over HDMI while showing DisplayPort (16 of 16 reads, all `0F`), so it listens on an inactive input. The switch back was confirmed on the first try. It didn't jump back by itself.<br>MSI over DisplayPort, run 1: didn't switch away at all, because the MSI ignores commands sent over DisplayPort. | Same |
| Time per switch | MSI over HDMI: the command takes about 65 ms; the MSI reports the new input about 150 ms after it, and the switch back was confirmed within 400 ms. The picture takes longer to appear (not measured). Reading the current input takes about 60 ms; reading the capabilities string takes 1.4 to 2.8 s. | Loading feedback in the UI |

**Decision (2026-11-20):** Switchboard is for the **MSI G27CQ4** (monitor 1 in the probe). Use case: switch the MSI between this PC and a **PS5**, clicking the switch on another monitor while the MSI shows the PS5.

**Brightness test on the MSI (2026-11-20):** no visible change. Windows accepted the command in 55 ms; the read-back got no answer. Together with switch run 1: **over DisplayPort the MSI doesn't act on DDC/CI commands at all, even though Windows reports success.**

**Re-check with repeated commands (2026-11-20):** `-DimTest` now resends the dim command every half second for 4 seconds, in case a single command hit a bad stretch. On the MSI: 7 sent, all 7 accepted by Windows, no visible change, no read-back. So over DisplayPort the MSI ignores commands entirely, not just some of the time.

**MSI over HDMI (2026-11-20):** the owner moved the MSI to its HDMI 1, through the cable from Acer `06F0`, which Windows reports as DVI. That Acer is unplugged for now. **The MSI works over HDMI.** Discovery read its capabilities string and current input on the first try, and `-DimTest` dimmed it (read-back `10`) and restored it. So the problem is the MSI's DisplayPort side. Whether DisplayPort DDC/CI works on this PC in general is unknown, since no other monitor here has DisplayPort.

**Cabling plan (2026-11-20):** Switchboard needs this PC on one of the MSI's HDMI ports, but the graphics card's two HDMI ports drive the two Acers, and the Acers have no DisplayPort. Plan: connect one Acer from a free graphics-card DisplayPort with a DisplayPort-to-HDMI cable. A cheap passive one is enough for the Acer's 1080p at 75 Hz. That frees a graphics-card HDMI port for the MSI.

- PC to MSI HDMI 1 (`11`), PS5 to MSI HDMI 2 (`12`). Switchboard switches between `11` and `12`.
- Cost: on HDMI the MSI advertises 2560x1440 at up to **144 Hz**, against 165 Hz on DisplayPort (from the EDID modes Windows lists for its HDMI input).
- Not recommended: the DisplayPort-to-HDMI cable on the MSI itself. 1440p at 144 Hz needs an HDMI 2.0-class adapter, and active adapters don't always pass DDC/CI through.
- Without buying anything: keep the MSI on DisplayPort and switch with the Navi Key's Input Source button. With the current settings, Switchboard can't control the MSI over DisplayPort.

**Keeping DisplayPort (owner's preference, 2026-11-20): not ruled out.** The MSI answered some reads correctly over DisplayPort, so it implements DDC/CI there; something makes it unreliable. Research: "works on HDMI, not on DisplayPort" is common, and the cause varies (monitor, driver, settings); see [ddc-ci-notes.md](ddc-ci-notes.md#displayport). Experiments, cheapest first. Measure each with `-ReadTest` on the MSI (40 of 40 over HDMI). If reads become reliable, confirm with `-DimTest`.

1. **Baseline:** cables back to the normal setup (MSI on DisplayPort), run `-ReadTest`. **Done 2026-11-20: 5 of 40** (input 5 of 20, brightness 0 of 20; mostly `0xC0262589`). The MSI is monitor 3 in the normal layout.
2. **G-SYNC off:** NVIDIA Control Panel, Set up G-SYNC, untick Enable. A theory, not a confirmed fix. The owner has FreeSync on in the MSI's menu. It was on during the HDMI tests too, which worked, but NVIDIA most likely runs variable refresh with this monitor only over DisplayPort (G-SYNC Compatible). That makes active variable refresh the main difference between the working HDMI connection and the DisplayPort one.
3. **FreeSync off** in the MSI's on-screen menu (currently on). Stronger than step 2: the monitor stops offering variable refresh at all.
4. **60 Hz** in Windows display settings, temporarily.
5. **Power-cycle the MSI:** pull its power for 30 seconds.
6. **Another DisplayPort cable** or another graphics-card DisplayPort port.
7. **Update or roll back the NVIDIA driver** (Windows reports 32.0.16.1714).
8. **NVIDIA's own I2C interface** (NVAPI) instead of the Windows DDC/CI API. A bigger step: needs a new probe, and it's NVIDIA-only.

If none of these work, use the HDMI cabling plan above.

**Checked (2026-11-20):** neither Acer has a DisplayPort port, so Acer `0673`'s capabilities string lists an input (`0F`) that doesn't exist. The MSI does the same with `10` (DisplayPort 2). See [ddc-ci-notes.md](ddc-ci-notes.md#known-problems).

**Switch test on the MSI over HDMI:**

1. Other input empty: **done, works** (run 2 above).
2. Other device active: **next (owner), once the cabling plan is in place.** With the PS5 on HDMI 2 and turned on, run `-Monitor <MSI> -SwitchTo 0x12`. This is the real use case.

If the screen doesn't come back after a switch, the right side of the Navi Key opens Input Source by default.

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
  - It must be reachable while the switched monitor shows the other device. On Windows 11, tray icons appear only on the main display's taskbar, so if the MSI is the main display, the tray icon disappears along with the picture. Options: make an Acer the main display, rely on hotkeys, or add a small switch window that can sit on another monitor.
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
