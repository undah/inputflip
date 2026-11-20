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
- **Some monitors answer only some of the time.** The owner's MSI G27CQ4 on DisplayPort (no DDC/CI option in its menu) goes through stretches where every read works (8 of 8) and stretches where none do (0 of 48), failing with `0xC0262589` (invalid command field) or `0xC026258B` (bad checksum). Pausing longer between commands didn't help. Successful reads return correct values. Its capabilities string never came through. **Over HDMI the same monitor answers every time and acts on commands**, so the problem is its DisplayPort side. Before giving up on a monitor, try the other cable type. Either way, the app must:
  - retry reads with backoff and treat a failed read as "unknown", not "wrong";
  - work without the capabilities string (MCCS table plus manual values);
  - not assume a switch failed just because the read-back failed. Corrupted *replies* don't mean the *command* didn't arrive.
- **The capabilities string can list inputs the monitor doesn't have.** The owner's Acer VG240Y (EDID `ACR 0673`) lists `60(01 0F 11)`, including DisplayPort, but it has no DisplayPort port. The MSI G27CQ4 lists `10` (DisplayPort 2) but has one DisplayPort port. They probably report for the whole model family. Treat detected inputs as suggestions the user can hide or rename, not a list of real ports.
- **A successful `SetVCPFeature` call doesn't mean the monitor did anything.** On the owner's MSI over DisplayPort, Windows reported success in about 50 ms for both an input switch and a brightness change, and neither had any visible effect. The app can't show "switched" based on the call alone: confirm with a read-back where the monitor supports it, and otherwise show the result as unconfirmed.

## DisplayPort

DDC/CI over DisplayPort is often less reliable than over HDMI. "Works on HDMI, not on DisplayPort" is a common report, and the cause varies:

- Some vendors only implement DDC/CI on HDMI inputs ([ddcutil wiki](https://github.com/rockowitz/ddcutil/wiki/Samsung)).
- GPU drivers matter. On DisplayPort, DDC/CI goes through the driver's I2C-over-AUX path, and NVIDIA driver versions have broken DDC/CI for some monitors ([NVIDIA forum](https://forums.developer.nvidia.com/t/gddccontrol-issues-with-nvidia-drivers-i2c-monitor-display-ddc-dp-hdmi-failing/30427?page=4)).
- Docks, MST hubs, KVMs and adapters can block it.

The owner's MSI G27CQ4 is a partial case: on DisplayPort some reads work with correct values, but commands have no effect. It works fully on HDMI. The experiments to try are in [plan.md](plan.md), stage 1.

**Alternate route on NVIDIA:** NVAPI's I2C functions (`NvAPI_I2CWrite` / `NvAPI_I2CRead`, [NVAPI docs](https://docs.nvidia.com/nvapi/group__i2capi.html)) talk to the monitor without the Windows DDC/CI API. [monitorinputwizzard](https://github.com/lunat1q/monitorinputwizzard) uses `NvAPI_I2CWrite` to device `0x6E` to switch inputs on monitors that ignore the standard path. It's NVIDIA-only and untested here. No license is stated in the repo, so read it for ideas only; don't copy its code.

## Switch-back problem

Some monitors stop listening to DDC/CI on an input once it's no longer the one on screen. The PC can switch the monitor *away* but not *back*.

**Test first:** many monitors still accept commands on an inactive input. If yours does, there's nothing to fix. The owner's MSI G27CQ4 does, over HDMI: it answered 16 of 16 reads while showing another input and switched back on the first command. Tested with the other input empty; the test with a device active on it is still to do.

Workarounds, easiest first:

1. **Monitor's auto input switching** ("Auto Input" / "Auto Source" in the on-screen menu): when the input on screen loses its signal, the monitor jumps to one that has a signal. Free, but only works when the other device stops sending a signal.
2. **Send the switch-back from the other device:** works if the other input is also a computer. Consoles and streaming boxes can't.
3. **A different control path:** some monitors (often ones with a built-in KVM or USB-C hub, e.g. some Dell, Gigabyte and MSI models) accept control over USB, independent of the active input. Model-specific.
4. **External HDMI switch (most reliable):** everything plugs into a switch, the monitor stays on one input. Choose a switch controllable over USB, RS-232, IR or network so the PC can drive it.
5. **Hardware hacks:** a USB IR blaster replaying the monitor remote's input codes, or a microcontroller (e.g. Arduino) wired to the input button. Works on anything, but means opening the case.
