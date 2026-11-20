# DDC/CI notes

## What DDC/CI is

Most monitors support **DDC/CI** (Display Data Channel / Command Interface), a control channel that runs over the HDMI or DisplayPort cable. Software can use it to send the monitor commands such as "change brightness" or "switch input". The commands are defined by the VESA **MCCS** standard as numbered "VCP codes".

Input selection is VCP code **`0x60` (Input Source)**.

## Standard input values (MCCS)

| Value | Input |
|---|---|
| `0x01` | VGA 1 |
| `0x03` | DVI 1 |
| `0x0F` (15) | DisplayPort 1 |
| `0x10` (16) | DisplayPort 2 |
| `0x11` (17) | HDMI 1 |
| `0x12` (18) | HDMI 2 |
| `0x1B` (27) | USB-C (common, not universal) |

Some manufacturers (notably LG, and some Dell and Samsung models) use their own values. Always read the monitor's capabilities string and the current value to find out what a given monitor uses.

## Windows API

All in `dxva2.dll` (plus `user32.dll` for enumeration):

| Function | Use |
|---|---|
| `EnumDisplayMonitors` (user32) | List logical monitors |
| `GetNumberOfPhysicalMonitorsFromHMONITOR` | Count physical monitors behind one |
| `GetPhysicalMonitorsFromHMONITOR` | Get handles for DDC/CI |
| `GetCapabilitiesStringLength` / `CapabilitiesRequestAndCapabilitiesReply` | Read supported codes and values |
| `GetVCPFeatureAndVCPFeatureReply` | Read a value (e.g. current input) |
| `SetVCPFeature` | Write a value (e.g. switch input) |
| `DestroyPhysicalMonitors` | Release handles |

Python alternative for quick tests: the `monitorcontrol` package. Command-line alternative: NirSoft ControlMyMonitor (`ControlMyMonitor.exe /SetValue Primary 60 17`).

## Known problems

- DDC/CI often has to be turned on in the monitor's on-screen menu.
- Calls are slow (tens to hundreds of ms) and occasionally fail; retry.
- Some docks, adapters and KVM switches don't pass DDC/CI through.
- TVs generally don't support DDC/CI. Some support HDMI-CEC, but most PC GPUs can't send CEC.
- Reading the capabilities string is slow: 1.8 to 2.8 s on the owner's Acers, against about 60 ms for a single VCP read. Read it once in the background and cache it per monitor.
- **Physical monitor handles can be `0`.** The owner's primary monitor gets handle `0` from `GetPhysicalMonitorsFromHMONITOR`. Don't use `IntPtr.Zero` to mean "no handle"; track that separately. The first version of the probe got this wrong.
- A monitor that answers *every* call with `0xC0262589` (invalid command field) or `0xC026258B` (bad checksum) isn't talking DDC/CI at all; retrying doesn't help. Seen on the owner's MSI G27CQ4 (0 of 48 reads worked). Show the "turn on DDC/CI" message instead of retrying forever.

## Switch-back problem

Some monitors stop listening to DDC/CI on an input once it's no longer the one on screen. The PC can switch the monitor *away* but not *back*.

**Test first:** many monitors still accept commands on an inactive input. If yours does, there's nothing to fix.

Workarounds, easiest first:

1. **Monitor's auto input switching** ("Auto Input" / "Auto Source" in the on-screen menu): when the input on screen loses its signal, the monitor jumps to one that has a signal. Free, but only works when the other device stops sending a signal.
2. **Send the switch-back from the other device:** works if the other input is also a computer. Consoles and streaming boxes can't.
3. **A different control path:** some monitors (often ones with a built-in KVM or USB-C hub, e.g. some Dell, Gigabyte and MSI models) accept control over USB, independent of the active input. Model-specific.
4. **External HDMI switch (most reliable):** everything plugs into a switch, the monitor stays on one input. Choose a switch controllable over USB, RS-232, IR or network so the PC can drive it.
5. **Hardware hacks:** a USB IR blaster replaying the monitor remote's input codes, or a microcontroller (e.g. Arduino) wired to the input button. Works on anything, but means opening the case.
