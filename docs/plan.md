# Plan and history

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
| Switch-back works (other device active)? | MSI over HDMI, from the app 1.0.5 (2026-11-21, after the cabling plan, PS5 on): **yes.** PC (HDMI 2, `12`) → PS5 (HDMI 1, `11`) at 18:50:04 and back at 18:50:10, each confirmed 150 ms after the command. No screens-off step. Owner confirmed both. | Whether a workaround is needed |
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

**PS5 → PC failed after a manual switch (2026-11-21, 11:50 to 11:54, installed build).** The owner put the MSI on the PS5 with the Navi Key, then clicked DisplayPort 1 three times (11:50:42, 11:50:48, 11:51:26): ignored. Every read-back got `0xC0262589` (the MSI answering garbled), unlike the switch-backs that worked (11:23:53, 11:25:55, 11:41:53), where reads right after the command failed with `0xC0262582` (no answer) and Windows then saw the DisplayPort link come back (`0xC026258D`). `quiet-link-test.ps1 -Target 0x0F` (screens off, 6 sends accepted) didn't bring it back either. The MSI stayed lit through it, as expected: it was showing the PS5's picture. Not yet known: whether switch-back still works when the *app* made the switch to the PS5 (every switch-back that worked came after one), or whether the PS5 being on matters. Leading suspect: the manual switch. Then (11:55) the owner clicked PS5 (screens-off sequence, 11:55:22 to 11:55:28) and DisplayPort 1 at 11:55:31: ignored too. But that click went out 3 s after the screens came back, while the app was re-finding monitors (11:55:31.335), like the other failure after an app switch (11:43:33, 0.2 s after re-finding). The switch-backs that worked were all sent 19 s or more after the last re-find. Second suspect: a command sent too soon after the screens-off sequence. **Confirmed (11:58):** the owner clicked PS5, waited 30 s, clicked DisplayPort 1: the MSI came back. **Fix in 1.0.1:** for 25 s after a screens-off switch, a switch on that monitor re-sends the command every 2 s (plain commands it ignores do no harm) and stops at the first display change more than 5 s after the screens came back. Every switch-back that worked was followed by one. Targets that need the screens-off sequence wait out the 25 s instead. The log records how many seconds after the screens came back the display change came, which will narrow the real limit down. **Tested with 1.0.1 (12:08, owner: "came back quickly"):** first send at 4.4 s after the screens came back, display change at 7.0 s, so one send was enough. Recomputed from the wake time, the failures were at 1.0, 1.4 and 1.7 s: the MSI listens again somewhere between 1.7 and 4.4 s. Then a click on PS5 at 13.8 s was held until 25 s for nothing. **1.0.2:** a confirmed switch-back ends the waiting period, and a screens-off switch only waits until 2 s after the screens came back (its sends come 3 s later still). Still open: switching by hand with the Navi Key and then back from the app failed even 44 s later and with the screens off; until that's understood, switch to the PS5 from the app. Fallback if PS5 → PC over DisplayPort proves unreliable: the HDMI cabling plan below (over HDMI the MSI answered every time).

**Shortening the screens-off switch (2026-11-21, 12:40 to 14:10).** Goal: less than the app's ~7.4 s of dark screens (3 s off, 6 sends 0.5 s apart, 1.5 s, wake). `scripts/quiet-link-test.ps1` now runs timed round trips (screens off, sends, wake, ask the owner, switch back with plain `0x0F` sends every 2 s from 5 s after waking, like the app) and logs a timeline to `logs\quiet-link-test.log` (not committed). Target `0x11`, PS5 on HDMI 1, Switchboard closed. "Claude" = the Claude desktop app's terminal panel, "PS" = Windows PowerShell from the Start menu. Times are actual (the script's sleeps overshoot by about 0.25 s).

| Time | Where | Sends | Dark | To PS5 | Back to PC |
|---|---|---|---|---|---|
| 12:40 | Claude | app timings, reading every 0.1 s (`-Watch`) | 7.4 s | 0 of 2 | |
| 12:42 | Claude | one at 1.3 s | 2.8 s | 0 of 3 | |
| 12:43 | Claude | one at 0.75 s | 2.3 s | 0 of 3 | |
| 12:52 | app 1.0.4 | app timings | 7.4 s | 1 of 1 | |
| 12:54 | Claude | app timings | 7.6 s | 0 of 2 | |
| 13:05 | Claude | app timings | 7.7 s | 2 of 2 | 2 of 2 |
| 13:07 | Claude | one at 2.25 s | 3.8 s | 1 of 3 | 1 of 1 |
| 13:10 | Claude | 10 from 1.5 s, 0.3 s apart, `-Watch` | 6.0 s | 0 of 3 | |
| 13:12 | Claude | 10 from 1.75 s, 0.3 s apart | 6.2 s | 0 of 3 | |
| 14:05 | PS | 10 from 1.8 s, 0.3 s apart | 6.2 s | 3 of 3 | 1 of 3, 1 unclear |
| 14:07 | PS | **4 from 2.75 s, 0.3 s apart, wake 0.5 s after the last** | **4.3 s** | **5 of 5** | 3 of 5 |

- **1.0.5 uses the 14:07 setting:** screens dark for 4.3 s instead of 7.4 s. The app waits 2.75 s, the time the script's first send actually went out. Margin unknown: 3.8 s of dark with one send at 2.25 s switched 1 time in 3 (in the Claude terminal, see next point).
- **Run these tests from Windows PowerShell, not the Claude terminal.** With 4.3 s of dark or more and no reads, PC → PS5 failed 5 of 7 rounds in the Claude terminal and 0 of 8 in Windows PowerShell, and the app (not started from Claude) hasn't failed once since 12:08. Cause unknown; perhaps something in Claude's process tree keeps the displays or the DisplayPort link awake. Earlier notes put these failures down to "spells" of the MSI; for PC → PS5 that doesn't hold once the Claude-terminal rounds are set aside.
- **Reading during the dark spell (`-Watch`) failed every time** (0 of 5), but only ever ran in the Claude terminal.
- **The MSI may switch to the PS5 by itself.** At 14:05 round 2, Windows reported a display change 0.3 s after the screens went off, so every send failed with `0xC026258D` (handle gone, nothing reached the monitor), and the MSI still went to the PS5. Likely an automatic input switch when DisplayPort goes dark. To confirm: a round with no sends (`-Sends 0 -OffSeconds 4.5 -WakeMs 0`), and look for an auto-scan option in the MSI's menu.
- **The switch back to the PC is the unreliable part.** The script's plain sends (7 to 8 s after waking, every 2 s for 20 s) failed 3 times in 8 in Windows PowerShell; the app's failed at 11:50, 12:40 and 12:53. It lines up with the reads right after waking: where the MSI answered `0x11` throughout, the switch back worked (6 of 6); where its answers turned garbled (`0xC0262589`, `0xC026258B`), it failed (3 of 3) and stayed failed until the owner used the Navi Key (at 12:40 for 22 minutes). So the earlier "manual switch" and "spells" suspects were most likely this. Ideas: when the answers are garbled, switch back with the screens off as well (tried once at 11:5x without success, possibly from the Claude terminal), or the HDMI cabling plan.
- **1.0.5 no longer re-finds the monitors after a screens-off switch.** The switch caused no display change in the test rounds and the handle kept working; the re-find read the MSI right after waking and, on a garbled answer, cleared the input just assumed.
- **Firmware (checked 2026-11-21):** MSI's G27CQ4 support page offers no firmware, only a 2019 monitor driver, Display Kit 0.0.2.3 and a Google Play item; its tabs are Driver and Utility & Others. MSI's firmware-over-DisplayPort tool covers only some QD-OLED models. So FW.013 stays.

**Superseded (2026-11-21, 18:50):** the MSI moved to HDMI (cabling plan below) and now switches both ways in 150 ms, confirmed, with no screens-off step. The DisplayPort findings above stay as notes for monitors with the same quirk; the screens-off switch is still in the app for them.

**Stage 1 was thought done** (the way back isn't reliable, above): the MSI switches between the PC (DisplayPort) and the PS5 (HDMI) in both directions with the PS5 on, without new cables.

**Test to confirm (owner, at the monitor):**

1. Put the MSI on the PS5 with the Navi Key. Turn the PS5 on.
2. In Switchboard click **DisplayPort 1** once. Wait 10 s. Did the PC come back?
3. Repeat step 1, then click **DisplayPort 2** once. Wait 10 s.
4. Do steps 1 to 3 again to see if it's repeatable.
5. With the PC on screen, click **HDMI 2** once (PS5 on). Wait 10 s. Did it switch?

**Decision (2026-11-20): HDMI plan.** Open again as of 2026-11-21: over DisplayPort the switch back to the PC isn't reliable (above). The owner chose it over the DisplayPort long shots. Setup checklist (owner):

1. Buy a DisplayPort-to-HDMI cable. A passive one is enough for an Acer (1080p, 75 Hz).
2. Connect one Acer from a free graphics-card DisplayPort port to its HDMI input.
3. Connect the MSI to the freed graphics-card HDMI port, on the MSI's HDMI 1. Connect the PS5 to the MSI's HDMI 2.
4. In Windows, set the MSI to 144 Hz (its HDMI maximum).
5. Run `ddc-probe.ps1` discovery, then with the PS5 turned on: `-Monitor <MSI> -SwitchTo 0x12`. That's the last stage 1 test (switch-back with a device active on the other input).

**Done (2026-11-21, 18:40 to 18:50).** What was actually set up:

- Cable: BENFEI DisplayPort-to-HDMI, 1.8 m, "4K@30Hz max" with a converter chip. On Acer `06F0` (VG240Y), from a graphics-card DisplayPort port. Not on the MSI: too slow for 1440p at 144 Hz (about 60 Hz at most), and a converter chip may not pass DDC/CI. Acer `06F0` still answered DDC/CI through it in discovery (one garbled read in the app).
- The MSI on the freed graphics-card "HDMI port", into the **MSI's HDMI 2** (PC, `12`); the PS5 stayed on HDMI 1 (`11`). The MSI's DisplayPort cable unplugged.
- Windows reports the MSI's link as DVI (`outputTechnology` 4), like that port did for the Acer before; `PcInputDetector` counts DVI as HDMI.
- **Correction (2026-11-21, 19:45):** that wasn't an HDMI port. The RTX 4070 has one HDMI port and three DisplayPort ports; Acer `06F0`, and then the MSI, were on a DisplayPort port through a DP-to-HDMI cable, which Windows reports as DVI and which limits the MSI to 60 Hz. At 19:50 the owner swapped the monitor ends of the MSI's and Acer `0673`'s cables: the MSI is now on the real HDMI port at 144 Hz, Acer `0673` on the DP-to-HDMI cable.
- Discovery (reads only): all three monitors answered on the first try; the MSI's capabilities string came through over HDMI in 2.6 s, current input `12`.
- Owner's settings.json, MSI entry, edited by hand: `blankScreensWhenLeaving` removed, `pcInput` `0x12`, HDMI 2 shown and first with Alt+PageDown (moved from DisplayPort 1), both DisplayPort inputs hidden.
- Test from the app (1.0.5, PS5 on): both directions confirmed in 150 ms (results table above).
- Windows made an Acer the main display after the rewiring; the owner was told how to make the MSI main again (Settings › Display › Multiple displays) and to set 144 Hz.

**Stage 1 is done.**

**Checked (2026-11-20):** neither Acer has a DisplayPort port, so Acer `0673`'s capabilities string lists an input (`0F`) that doesn't exist. The MSI does the same with `10` (DisplayPort 2). See [ddc-ci-notes.md](ddc-ci-notes.md#known-problems).

**Switch test on the MSI over HDMI:**

1. Other input empty: **done, works** (run 2 above).
2. Other device active: **done, works** (2026-11-21, from the app; PS5 on HDMI 1, PC on HDMI 2).

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
  - [x] **Update button (1.0.3, 2026-11-21).** Owner chose this PC's `releases\` folder as the source (repo is
        private, so not GitHub). The build bakes the folder path in; settings.json `updateSource` overrides it. The app
        checks at start and on Refresh; an accent **Update** button appears in the title bar when a newer version is
        there, and Settings › About has *Check for updates* / *Install and restart*. Velopack `UpdateManager`, full
        packages. Friends would need a shared source (a public URL or a synced folder) set as `updateSource`.
    - [x] **Owner test (12:17):** installed 1.0.3, which found 1.0.4 in `releases\` 0.3 s after starting; Update
          clicked at 12:17:47, 1.0.4 running at 12:17:50 (owner confirmed, log agrees).
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

### Fast PC ↔ PS5 switch (1.0.6, 2026-11-21 evening)

Goal (owner): the MSI's switch between the PC and the PS5 as fast and smooth as it can be, well under a second. The
owner's answers (18:55): add a toggle key and keep Alt+PageDown / Alt+PageUp; the picture already comes back in
**under 1 s** in both directions; yes to moving the mouse off the MSI while it shows the PS5 and back afterwards; after
a switch that worked, just move the highlight (no status message).

**Where the time went in 1.0.5** (log of 18:50, code reading, read-only scripts at 19:00 to 19:08):

| Step | 1.0.5 | Notes |
|---|---|---|
| Key press to command | ~0 ms, unless another DDC/CI call is in flight | A log line was written on the UI thread before the command |
| `SetVCPFeature` | ~65 ms | The monitor gets it at the start; the rest is dxva2's 50 ms wait after a write |
| Monitor switches, picture | under 1 s (owner) | The MSI's own work; nothing the app can speed up |
| Read-back | first read 150 ms after the command, ~60 ms each | Confirmed on the first read both times (288 ms from command to "switched") |
| Window | highlight moved only after the read-back | Plus an 8 s status line |
| Input read, any monitor | 57 to 72 ms, median 62 to 64 | 20 of 20 on all three |
| Capabilities read at every start | 2.6 s (MSI alone), 5.6 s (all three at once) | Blocks every monitor, see below |

**Finding: DDC/CI calls to different monitors wait for each other on this PC.** An MSI read took 2.6 s while an
Acer's capabilities string was being read; with an Acer read in a loop, MSI reads took 90 ms instead of 62. So in
1.0.5 a key press in the first ~6 s after Switchboard started (or after an update) waited for the capabilities reads,
and the quick re-read on window activation (all three monitors, the two hidden Acers included) could land in front of a
click's switch. Details in [ddc-ci-notes.md](ddc-ci-notes.md#known-problems).

**1.0.6 changes:**

- [x] The command goes out before anything else; the window, the mouse and the log come after.
- [x] The highlight moves to the new input at once. A confirmed switch shows no status line (Narrator still hears it);
      problems and unconfirmed switches still do. A spinner appears only if the monitor hasn't confirmed after 400 ms.
- [x] Read-back starts right after the command and repeats every 100 ms for up to 2 s (was: first read after 150 ms).
      A newer switch on the same monitor ends the older one's read-back instead of having it report "didn't switch".
- [x] Capabilities strings are kept in `capabilities.json`, so a start doesn't read them again (checked with a
      `--data-dir --no-switch` copy: the second start read none). Failed ones are retried only on Refresh or a display
      change, not on every window activation.
- [x] The quick re-read on window activation waits 300 ms, skips hidden monitors, and doesn't run within 1 s of a
      switch.
- [x] **Toggle:** the next-input shortcut skips hidden monitors and ones with one input shown, so on the owner's PC it
      flips the MSI between PC and PS5. Set to **Alt+End** on the MSI in the owner's settings.json (19:09; 1.0.5 took
      it too). It re-reads the input first (about 60 ms) only when the last reading is over 30 s old, in case the MSI
      changed input by itself.
- [x] "Already showing" is checked with the monitor (one read) before saying so, instead of trusting memory.
- [x] **Mouse:** leaving the PC's input with the mouse on that monitor moves it to the middle of Switchboard's window
      (else another screen); switching back puts it where it was. Settings › General › *Move the mouse out of the way*
      (on by default; `leaveMouseAlone` in settings.json).
- [x] 81 unit tests (8 new: toggle monitor choice, toggle direction, parking spot). Build has no warnings.
- [ ] **Owner test with 1.0.6** (Refresh, then Update in the installed app; PS5 on, mouse on the MSI):
  1. Alt+End: PS5 shows, the mouse jumps to Switchboard's window. Alt+End again: PC back, mouse back where it was.
  2. Alt+PageUp, then Alt+PageDown: same, without the toggle.
  3. Click PS5 and PC in the window: the highlight moves at once, no status message.
  4. Press Alt+End twice quickly: it should end up back on the PC with no "didn't switch" message.
  5. With a game in front on the MSI: Alt+End both ways.
  6. Then read the times in the log (`command out N ms after ...`, `confirmed N ms after the command`).

**Owner tests with 1.0.6 (19:32 to 19:54, PS5 on):**

| Time | How | Command out after key | Confirmed after command | Picture (owner) | PC side (recorder) |
|---|---|---|---|---|---|
| 19:32:04 to 19:32:39 | toggle, MSI on the DP-to-HDMI cable | 0 to 8 ms | 115 to 124 ms (one 292 ms: first read garbled) | | |
| 19:37:08 to 19:37:56 | toggle, same cable | 0 to 71 ms | 131 to 300 ms | PS5 after 6 to 7 s; all three screens black about 5 s on the way back | nothing: no display change, no gap in any screen's picture |
| 19:52:56, 19:53:01 | toggle, MSI on the real HDMI port at 144 Hz | 0 ms | 144 ms, 129 ms | PS5 after about 6 s; black about 5 s on the way back | on the way back, the MSI unplugged itself 0.5 s after the command for 0.8 s; all three screens reset (250 and 470 ms without picture) |
| 19:53:37, 19:53:57 | the MSI's joystick | | | about the same | the MSI unplugged itself in both directions, same resets |

The toggle key: the owner moved it to Ctrl+Tab, then Alt+Q (both work; Ctrl+Tab took tab switching away from every
app). The mouse move wasn't checked by eye.

**What this means:** Switchboard's part is now about 0.13 s from key to confirmed, against about 0.29 s in 1.0.5, and
the highlight moves at the key press. The 5 to 6 s the owner sees is the MSI's input change itself: it unplugs itself
from the input it switches to, so the PS5 starts its HDMI setup over and Windows resets every screen. Its own joystick
does the same. Details in [ddc-ci-notes.md](ddc-ci-notes.md#the-msi-over-hdmi-2026-11-21). Not fixable through
DDC/CI; the remaining lever is hardware that holds the hot-plug signal (untested, and it must pass DDC/CI through).

**1.0.7 (19:57):** the replug moved the mouse onto an Acer after 1.0.6 had put it back, and killed the MSI's monitor
handle for about 3 s. Now the mouse goes back again when the MSI returns, and a switch that fails within 10 s of a display
change finds the monitors again and retries once. Mouse moves are logged.

**Owner test with 1.0.7 (20:02 to 20:04, updated with the Update button at 20:01:51):** the start took the capabilities
strings from `capabilities.json` (no 5.6 s of reading after the update). Alt+Q to the PS5 at 20:03:57: mouse moved off
the MSI to the middle of Switchboard's window on the left Acer, command out 72 ms after the key (the toggle re-read the
input first: last reading 75 s old), confirmed 123 ms after the command. Alt+Q back at 20:04:04: mouse back at the same
spot, command out 1 ms after the key, confirmed 123 ms after it; the MSI's replug followed, and Switchboard found the
monitors again by itself at 20:04:08. Owner: "mouse came back". Windows didn't move the mouse during this replug, so the
second put-back had nothing to do; the retry after a display change wasn't needed.

- [x] Toggle, per-input shortcuts, instant highlight, quiet confirm, mouse parking: working (owner, 19:32 to 20:04).
- [ ] Faster than the MSI's own 5 to 6 s: needs hardware that holds the hot-plug signal (see above). **Owner's decision
      (2026-11-21, 20:06): stop here**, no hardware for now. Done for the fast-switch task.

**Done when:** you'd be happy for a friend to install it. Left: the owner checks above, and the auto-update decision.

---

## Stage 4: InputFlip, open source (started 2026-11-21, 20:15)

**Goal (owner):** new features, a look of its own, and a public, well-documented open-source repo that people searching
for a monitor input switcher can find.

**Owner's choices (2026-11-21, 20:20, and 2026-11-22):**

- **Name: InputFlip** ("Switchboard" is the name of 2,300 GitHub repos; "InputFlip" of none). Everything people see is
  InputFlip: window titles, Start menu, the exe (`InputFlip.exe`), the code (`src/InputFlip`, namespaces `InputFlip.*`),
  the repo (`undah/inputflip`). Kept so 1.0 installs keep working: the Velopack pack id `SwitchboardApp` (install
  folder `%LOCALAPPDATA%\SwitchboardApp`), and the data folder `%LOCALAPPDATA%\Switchboard` where it has settings
  (new installs use `%LOCALAPPDATA%\InputFlip`). The start-with-Windows value moves from `Switchboard` to `InputFlip`.
- **Licence: MIT.**
- **Look: "Tiles"** (picked from three mockups: Signal, Tiles, Studio). Each input is a big tile with its icon; tiles
  flip when you switch. First built in coral on plum; **the owner asked for a clean blue instead (2026-11-22)**: the
  input showing now is blue `#2563EB` with white text, on near-black `#10141B` (dark) or `#F7F9FC` (light). The blue
  also replaces the Windows accent color in standard controls. Owner also flagged the logo as low quality: it's now
  drawn as vector shapes in the UI (`Controls/LogoMark.xaml`).
- **Releases on GitHub** (2026-11-22; before, the Update button read this PC's `releases\` folder). Installed copies
  check https://github.com/undah/inputflip/releases; `build-installer.ps1 -Publish` creates a release.
- **History:** author email rewritten to the GitHub noreply address before going public; old branch
  `stage1-probe-script` (fully in `main`) deleted; repo renamed `undah/switchboard` → `undah/inputflip`.

**Features (all eight offered were picked):**

- [x] Scenes: one click, key, picker entry or command switches several monitors (and sets brightness); again goes back
- [x] Command line and `inputflip://` links (scripts, desktop shortcuts, AutoHotkey, Stream Deck), sent to the running
      copy over a named pipe; `--list`; exit codes 0/1/2
- [x] USB rules: a USB device plugging in or leaving switches a monitor or runs a scene (software KVM)
- [x] Pop-up picker by the mouse (`Ctrl+Alt+Shift+P`), and a short note after a switch on a screen that didn't switch
- [x] Brightness and contrast per monitor (sun button), brightness in scenes
- [x] Monitor speaker volume and mute, where the capabilities string lists them
- [x] Tray icon with scenes and inputs in its menu; optional close to the tray
- [x] Lock, unlock, sleep and wake rules (off by default)
- [x] New look and icon (blue tiles; two tiles on blue for the icon)
- [x] Docs: README (search terms, FAQ, alternatives), user guide, command line, compatibility list, CONTRIBUTING,
      CODE_OF_CONDUCT, SECURITY, issue forms, PR template, CI workflow, architecture
- [x] History audit, owner's OK (2026-11-22), repo public at https://github.com/undah/inputflip with description, 20 topics and private vulnerability reporting; author email rewritten to the noreply address (60 commits, same tree), `stage1-probe-script` deleted
- [x] Shipped as 1.1.0: GitHub release v1.1.0 (https://github.com/undah/inputflip/releases/tag/v1.1.0), CI green. Owner updated
      from 1.0.7 with the Update button at 11:42:55 (2026-11-22); 1.1.0 started at 11:42:58 on the old data folder
      (`%LOCALAPPDATA%Switchboard`, log now `inputflip.log`). Velopack renamed the Start menu and desktop shortcuts to
      InputFlip, pointing at `currentInputFlip.exe`; the old `Switchboard.exe` launcher stays in the install root
      (harmless: it starts whatever is in `current`). `inputflip://` links registered; start with Windows was off and
      still is.

**Checked without the owner (2026-11-22):** 173 unit tests; build with no warnings; a `--data-dir --no-switch` copy
on the real monitors: the main window in dark and light, the Scenes and About pages, `--list`, `--switch` with an
unknown input (exit 1), `--brightness 150` (exit 2), `--switch` refused under `--no-switch`.

**Not checked yet (needs the owner, since it switches monitors or changes the PC):**

- [ ] A scene from its pill, its shortcut and the picker; pressing it again goes back
- [ ] `InputFlip.exe --switch MSI PS5` and `inputflip://toggle` from a browser or Stream Deck
- [ ] The tray menu, and close to the tray
- [ ] The note after a switch shows on an Acer, not the MSI
- [ ] Brightness slider on an Acer; volume on a monitor with speakers (none here)
- [ ] A USB rule with a real USB device; lock and unlock rules
- [x] The update from 1.0.7: shortcuts and links checked after the exe rename (2026-11-22)

## Later (ideas)

Each roughly half a day to a day:

- Some LG monitors: send input commands on their own DDC/CI address (0x50) through NVIDIA NVAPI or AMD ADL I2C
- Code signing, so SmartScreen stops warning
- ARM64 build (the project already has the platform)
- Translations
- Control an external HDMI switch (USB / RS-232 / IR / network) for monitors that can't switch back
- Arduino or IR-blaster support as a last-resort workaround
