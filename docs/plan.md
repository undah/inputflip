# Plan

Three stages. Each one ends with a clear "done when" check before moving on.

| Stage | Result | Estimate |
|---|---|---|
| 1. Proof of concept | We know whether and how the monitor responds to DDC/CI | 15–30 min |
| 2. Working app | Functional app with switching and hotkeys | A few hours |
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
2. **G-SYNC off:** NVIDIA Control Panel, Set up G-SYNC, untick Enable. **Already off (2026-11-20):** "Enable G-SYNC, G-SYNC Compatible" was unticked, and the panel says the MSI isn't validated as G-SYNC Compatible. So the baseline above was measured with G-SYNC off, and variable refresh from the graphics card isn't the cause. Original reasoning, kept for context: The owner has FreeSync on in the MSI's menu. It was on during the HDMI tests too, which worked, but NVIDIA most likely runs variable refresh with this monitor only over DisplayPort (G-SYNC Compatible). That makes active variable refresh the main difference between the working HDMI connection and the DisplayPort one.
3. **FreeSync off** in the MSI's on-screen menu (currently on). Stronger than step 2: the monitor stops offering variable refresh at all.
4. **60 Hz** in Windows display settings, temporarily.
   - **Steps 3 and 4 together (2026-11-20): 0 of 40, no help.** The MSI was confirmed at 2560x1440 60 Hz; nearly every error was `0xC0262589`. Owner to restore FreeSync and 165 Hz.
5. **Power-cycle the MSI:** pull its power for 30 seconds.
6. **Another DisplayPort cable** or another graphics-card DisplayPort port.
7. **Update or roll back the NVIDIA driver** (Windows reports 32.0.16.1714).
8. **NVIDIA's own I2C interface** (NVAPI) instead of the Windows DDC/CI API. A bigger step: needs a new probe, and it's NVIDIA-only.
   - **Built: `scripts/nvapi-probe.ps1` (2026-11-20).** Owner skipped steps 5 to 7 to try this first. Control on Acer `0673` over HDMI: reads 40 of 40, and a dim command landed (read back `10`, restored to 100).
   - **MSI over DisplayPort, reads: 0 of 40.** Every reply is `51 FF FF FF ...`: the PC's own source byte `51` echoed back, then nothing (`FF`). Same with reply delays of 50, 150 and 400 ms and I2C speeds default, 33 and 10 kHz. So over DisplayPort nothing from the MSI's DDC/CI side comes back on this route.
   - **MSI over DisplayPort, commands: no effect.** `nvapi-probe.ps1 -Display 3 -DimTest` (owner watching, 2026-11-20): no change, the same as through dxva2.

**Conclusion so far (2026-11-20):** over DisplayPort the MSI ignores DDC/CI commands on both routes (Windows dxva2 and NVIDIA's NVAPI I2C). Settings tried: G-SYNC (already off), FreeSync off, 60 Hz. Its replies are garbled through dxva2 (at best 5 of 40) and absent through NVAPI. Long shots left: steps 5 to 7 (power cycle, another cable or port, another driver). Otherwise use the HDMI cabling plan above. Over HDMI everything works.

**Switch back over DisplayPort worked once (2026-11-20, 22:32, stage 2 app).** The MSI was showing the PS5 on HDMI; the PC was on DisplayPort as usual. The owner clicked six inputs in the app in 22 seconds (`12`, `0F`, `10`, `11`, `0F`, `10`), and the MSI switched to DisplayPort (the PC): Windows reported the DisplayPort display coming back at 22:32:31, about 7 s after the last two clicks (`0F` at 22:32:21, `10` at 22:32:24). Every read-back failed (`0xC0262589`), so the app showed each switch as unconfirmed. Not yet known: which value did it, and whether a single click works.

**Hypothesis:** over DisplayPort the MSI acts on commands only while it **isn't** showing DisplayPort (no video stream on the link). All stage 1 DisplayPort tests ran while it showed DisplayPort. If this holds, PS5 → PC works without the HDMI cable, and only PC → PS5 needs another way (the Navi Key, or the HDMI plan).

**Other direction fails, as the hypothesis predicts (2026-11-20, 22:33).** With the MSI showing the PC on DisplayPort, the owner clicked HDMI 1 (22:33:31), HDMI 1 (22:33:42) and HDMI 2 (22:33:46): no switch. The first read-back worked and correctly reported `0F`, so the MSI heard the read and ignored the switch. DisplayPort 1 (already showing) was confirmed in 150 ms.

| Direction | Over DisplayPort |
|---|---|
| PS5 → PC (MSI showing HDMI) | Works (22:32) |
| PC → PS5 (MSI showing DisplayPort) | Ignored (22:33, three tries) |

Options for PC → PS5: the Navi Key (Input Source), the HDMI cabling plan below, or an experiment: make the DisplayPort link go quiet for a moment (stop sending a picture to the MSI) and send the command then.

**Quiet-link trick works (2026-11-20, 22:36).** `scripts/quiet-link-test.ps1` turned all screens off the way Windows does before sleep (`SC_MONITORPOWER`), waited 3 s, sent `60 = 12` (HDMI 2) six times over DisplayPort, then woke the screens with a mouse nudge. **The MSI came back on the PS5** (owner confirmed). Before: it read `0F`. After: it read `11` twice, which is wrong (it was on HDMI 2), so reads over DisplayPort stay untrustworthy.

So both directions work over DisplayPort, keeping 165 Hz:

| Direction | How |
|---|---|
| PC → PS5 | Screens off, send the command, screens on (about 8 s, all screens go dark) |
| PS5 → PC | Normal command |

**Built into the app:** per-monitor setting `blankScreensWhenLeaving` (the PC's input, `"0x0F"` for the MSI; also on the input's right-click menu). Switching from that input to any other uses the screens-off sequence. On for the MSI on the owner's PC. **Tested from the app (2026-11-20, 22:49): works both ways** (owner confirmed). The log shows three screens-off switches (`11` at 22:49:24 and 22:49:36, `12` at 22:49:47), each with all 6 sends accepted, about 6 s per switch. Getting back to the PC needs no screens-off step (proven at 22:32).

**PS5 → PC failed after a manual switch (2026-11-21, 11:50 to 11:54, installed build).** The owner put the MSI on the PS5 with the Navi Key, then clicked DisplayPort 1 three times (11:50:42, 11:50:48, 11:51:26): ignored. Every read-back got `0xC0262589` (the MSI answering garbled), unlike the switch-backs that worked (11:23:53, 11:25:55, 11:41:53), where reads right after the command failed with `0xC0262582` (no answer) and Windows then saw the DisplayPort link come back (`0xC026258D`). `quiet-link-test.ps1 -Target 0x0F` (screens off, 6 sends accepted) didn't bring it back either. The MSI stayed lit through it, as expected: it was showing the PS5's picture. Not yet known: whether switch-back still works when the *app* made the switch to the PS5 (every switch-back that worked came after one), or whether the PS5 being on matters. Leading suspect: the manual switch. Fallback if PS5 → PC over DisplayPort proves unreliable: the HDMI cabling plan below (over HDMI the MSI answered every time).

**Stage 1 is effectively done:** the MSI switches between the PC (DisplayPort) and the PS5 (HDMI) in both directions with the PS5 on, without new cables. The HDMI cabling plan isn't needed.

**Test to confirm (owner, at the monitor):**

1. Put the MSI on the PS5 with the Navi Key. Turn the PS5 on.
2. In Switchboard click **DisplayPort 1** once. Wait 10 s. Did the PC come back?
3. Repeat step 1, then click **DisplayPort 2** once. Wait 10 s.
4. Do steps 1 to 3 again to see if it's repeatable.
5. With the PC on screen, click **HDMI 2** once (PS5 on). Wait 10 s. Did it switch?

**Decision (2026-11-20): HDMI plan.** Superseded if the screens-off switch works reliably from the app (above): then no cable is needed. The owner chose it over the DisplayPort long shots. Setup checklist (owner):

1. Buy a DisplayPort-to-HDMI cable. A passive one is enough for an Acer (1080p, 75 Hz).
2. Connect one Acer from a free graphics-card DisplayPort port to its HDMI input.
3. Connect the MSI to the freed graphics-card HDMI port, on the MSI's HDMI 1. Connect the PS5 to the MSI's HDMI 2.
4. In Windows, set the MSI to 144 Hz (its HDMI maximum).
5. Run `ddc-probe.ps1` discovery, then with the PS5 turned on: `-Monitor <MSI> -SwitchTo 0x12`. That's the last stage 1 test (switch-back with a device active on the other input).

Then stage 1 is done.

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

- [x] **Project setup:** WinUI 3 app (`src/Switchboard`), single instance. First built as a tray app; changed to a normal window at the owner's request (see below).
- [x] **Monitor service** (`src/Switchboard.Core/Monitors`)
  - [x] Find monitors with `EnumDisplayMonitors` → `GetPhysicalMonitorsFromHMONITOR`
  - [x] Match each to a model name and serial from its EDID so settings stay attached to the right monitor after a reboot or cable swap. Reads the EDID Windows caches in the registry instead of WMI: faster, no extra package, and the parser is unit-tested.
  - [x] Read and switch inputs on a background thread: one serial queue per monitor, retries with backoff, timeouts. A switch is confirmed by reading the input back for up to 1.5 s; without a read-back it's reported as unconfirmed, not failed.
  - [x] Refresh the monitor list when displays are connected or disconnected (`WM_DISPLAYCHANGE`), and after waking from sleep.
- [x] **Settings store:** JSON file in `%LOCALAPPDATA%\Switchboard\settings.json` for monitor nicknames, input names, icons, hidden inputs, hotkeys and custom input values. Values are written as `"0x11"`. Comments and trailing commas are allowed. A file with a mistake is never overwritten, and edits reload by themselves.
- [x] **App window** (was "tray flyout"): list of monitors with their inputs. Click an input to switch; the current input is highlighted. Right-click an input to rename it, pick an icon or hide it.
  - It must be reachable while the switched monitor shows the other device. On Windows 11, tray icons appear only on the main display's taskbar, which disappears with the MSI's picture. **Decision (owner, 2026-11-20): a normal app window instead of a tray app**, kept open on an Acer. It has a taskbar button, opens where it was last left (first time: a monitor other than the main one), and closing it quits. The pin keeps it on top; the "…" button has Edit settings file and Open log. Hotkeys work too.
- [x] **Hotkeys:** global shortcut per input (`"hotkey"` on an input in settings.json), plus `Ctrl+Alt+Shift+I` to cycle the main monitor's visible inputs and `Ctrl+Alt+Shift+S` to open the window. The defaults avoid `Ctrl+Alt+letter`, which is AltGr on many keyboard layouts.
- [x] **Error handling:** plain messages such as "MSI G27CQ4 didn't respond. Check that DDC/CI is on in its menu, then try again." Raw errors go to `%LOCALAPPDATA%\Switchboard\switchboard.log` (tray menu: Open log).

### Stage 2 status (2026-11-20)

Built on `main`. Run it with `dotnet run --project src/Switchboard`; see the README.

**Verified on the owner's PC** (MSI still on DisplayPort, both Acers connected):
- Opens as a window on a side monitor; reopens where it was dragged (left Acer); a second launch brings the running copy to the front instead of starting another.
- Finds all three monitors with names from their EDID: `MSI G27CQ4` (`MSI3CB0-000000D1`), `Acer VG240Y` (`ACR0673-83001C4F`), `VG240Y` (`ACR06F0-TG5EE0018529`).
- Reads both Acers' capabilities strings (5.2 to 5.8 s each, in the background) and current inputs.
- The MSI's capabilities string fails over DisplayPort, as in stage 1, so the window offers the usual ports for it (HDMI 1 and 2, DisplayPort 1 and 2).
- The window renders as sketched, with the current input in the accent color.
- 39 unit tests pass (parsers, hotkeys, settings, input list).

**Not verified yet (owner):**
- [x] Clicking an input switches the MSI both ways with the PS5 on (2026-11-20; PC → PS5 with the screens off). The window can't confirm over DisplayPort, so it says the MSI "should now show" the input.
- [ ] Clicking an input on an Acer switches it, and the window confirms it
- [ ] Hotkeys, including cycle
- [ ] Rename, icon and hide from the right-click menu
- [x] Window remembers its position on an Acer after a restart (checked 2026-11-20)
- [ ] Pin (always on top) and minimize
- [ ] Sleep and wake, unplugging a monitor

Best done once the MSI is on HDMI (stage 1 cabling plan): over DisplayPort it ignores commands.

**Done when:** you can switch inputs from the tray and with hotkeys, and names and hotkeys survive a restart.

---

## Stage 3: Polished v1

**Goal:** something that feels like part of Windows 11.

Built 2026-11-21 while the owner was away, so nothing here was tested by switching a monitor. The plan predated two
decisions and was adapted: the app is a **normal window**, not a tray flyout (so "flyout position" became "opens where
you left it", already in stage 2), and the MSI uses the **screens-off switch** over DisplayPort.

### Steps

- [x] **Visual design**
  - [x] Mica background, Fluent icons (Segoe Fluent Icons), Windows accent color for the input showing now
  - [x] Light and dark mode follow Windows, or pick one in Settings › General. Checked with screenshots in both.
  - [x] Clear hover and pressed states, on the accent-filled row too (checked with screenshots of each state)
  - [x] Spinner while a monitor responds (stage 2). **Decided against the fade when the highlight moves:** WinUI 1.8's
        `BrushTransition`/`ScalarTransition` on the row crashed the app (access violation) when the theme changed.
  - [x] Spacing and corners from Windows 11 theme resources: monitors as cards, 4 px rows, 8 px cards. The window no
        longer has an empty strip at the bottom (it was sized as if it had a system title bar, 31 px too tall).
- [x] **"This PC" tag** (2026-11-21, owner asked which inputs have something connected). DDC/CI can't tell, so only
      the PC's own input is tagged: `PcInputDetector` (Core) matches the cable type Windows reports to the ports that
      fit it (HDMI/DVI, DisplayPort/USB-C, VGA), leaving out hidden inputs and ones marked console or TV. When two fit,
      the one on screen wins. Saved as `pcInput` once found, so it doesn't move when the monitor shows the PS5; the
      screens-off input counts as the PC's. Wrong guess: right-click › *This PC is plugged in here*. Checked with
      `--data-dir --no-switch` on the real monitors: Acer `0673` (HDMI) and Acer `06F0` (DVI cable, two HDMI ports,
      showing HDMI 1) both got `0x11`; the MSI shows it on DisplayPort 1 from its screens-off setting.
- [x] **Automatic input detection** from the capabilities string (stage 2). Manual **Add input** by value in
      Settings › Monitors. **No Test button** (owner's rule while away: no switching); you test by clicking the new
      input in the main window.
- [x] **First-run setup**, shown when settings.json doesn't exist, skippable on every page:
  1. Your monitors: found, cable type, whether each answers DDC/CI (this is the DDC/CI test; reads only)
  2. Name your inputs and pick an icon, untick unused ones
  3. Only when needed: turn on the screens-off switch for a monitor that answers over DisplayPort but lists no
     inputs (the MSI's pattern). The cable type comes from Windows (`QueryDisplayConfig`), not from the monitor.
  4. Shortcuts
- [x] **Settings window** (gear button or Ctrl+, in the main window)
  - [x] General: start with Windows (HKCU Run entry; tested on and off, **left off**), theme, keep on top, edit settings file
  - [x] Monitors: rename, reorder monitors and inputs, icons, show/hide, add input by value, remove added inputs,
        screens-off switch per monitor, forget a monitor that isn't connected
  - [x] Shortcuts: recorder that pauses Switchboard's own shortcuts while it listens, refuses a shortcut that's
        taken (by Switchboard or another app) and says by what; which monitor the next-input shortcut uses
  - [x] About: version, open log, open settings folder, run setup again, diagnostics with each monitor's
        capabilities string (copy one or all, read again)
- [x] **Accessibility**
  - [x] Keyboard: Tab through the title bar buttons and into the list, arrow keys between inputs (across monitors),
        Shift+F10 for an input's menu, F5 refresh, Ctrl+, settings. Focus rectangle visible. Checked by sending keys.
  - [x] Narrator: every focusable control has a name (checked with UI Automation on all windows and pages), inputs
        say "Switch to PS5" / "HDMI 1, showing now" with the port and shortcut as help text, status messages are
        announced (live region), headings on monitor names and page titles.
  - [ ] **Scaling, needs the owner:** all three displays are at 100%, and changing that is a system setting. Sizes
        are computed in DIPs and the window refits when moved to a display with other scaling. To check: set one
        Acer to 125% then 150% (Settings › Display › Scale), drag Switchboard onto it, open Settings and the setup
        (About › Run setup). Look for clipped text and the window's height.
- [x] **Packaging:** `scripts/build-installer.ps1` → `releases\SwitchboardApp-win-Setup.exe` (78 MB, includes .NET 10
      and the Windows App SDK; friends need nothing else) and a portable zip. Velopack 1.2.161, unsigned, not
      published. Real `.ico` (16 to 256 px) drawn by `IconArt` in Core. Start with Windows is in Settings.
  - [ ] **Auto-update: not done, owner's call.** Velopack can update from GitHub Releases, but the repo is private,
        so friends couldn't reach it. Options: make releases public somewhere (a public repo or any static folder),
        then add `UpdateManager` with that URL.
  - [ ] **Installer run, needs the owner:** not run on this PC (it installs, adds shortcuts and starts the app on the
        real settings). The packed app was started from the portable zip and works.
- [ ] **Testing checklist** (needs the owner; each switches monitors or changes the PC)
  - [ ] Install `SwitchboardApp-win-Setup.exe`, run it from the Start menu, uninstall it from Settings › Apps: the
        settings in `%LOCALAPPDATA%\Switchboard` should still be there afterwards
  - [ ] Sleep and wake: monitors come back in the window within a few seconds, shortcuts still work
  - [ ] Unplug and replug a monitor (an Acer): it disappears and comes back with its names
  - [ ] Two monitors: unplug one Acer and use the app
  - [ ] Non-100% display scaling (see Accessibility)
  - [ ] Hotkey for the input that's already showing (an Acer): status says it's already showing, nothing blinks
  - [ ] A shortcut for an input (Settings › Shortcuts), pressed while a game is in front
  - [ ] Clicking an Acer input switches it and the window confirms it (stage 2 leftover)
  - [ ] The MSI both ways again with this build (PC → PS5 uses the screens-off switch)
  - [ ] Start with Windows: turn it on, sign out and in

Also checked without the owner: 73 unit tests; the app builds with no warnings (`dotnet build Switchboard.slnx`);
first-run setup end to end in a temporary data folder (`--data-dir`, `--no-switch`), where it suggested and turned on
the screens-off switch for the MSI (`0x0F`); settings edits land in settings.json and the main window; shortcut
recorder saves, refuses a conflict, and the new shortcut works globally; live theme changes.

**Done when:** you'd be happy for a friend to install it. Left: the owner checks above, and the auto-update decision.

---

## Later (stage 4 ideas)

Each roughly half a day to a day:

- Auto-switch when a USB device connects or disconnects (KVM-style)
- Profiles (switch several monitors at once)
- Control an external HDMI switch (USB / RS-232 / IR / network) for monitors that can't switch back
- Arduino or IR-blaster support as a last-resort workaround
