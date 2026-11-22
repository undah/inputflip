# Monitor compatibility

Monitors people have tried InputFlip (or its DDC/CI commands) with. **Tried yours?**
[Add a report](https://github.com/undah/inputflip/issues/new?template=monitor_report.yml), especially if it didn't work.

How to read it:

- **Switches away:** the PC can switch the monitor to another input.
- **Switches back:** while the monitor shows another device, the PC can switch it back. Most monitors keep listening
  on the PC's cable; some don't (see [the switch-back problem](ddc-ci-notes.md#switch-back-problem)).
- **Inputs listed:** what the monitor's capabilities string lists for VCP `0x60`. Monitors often list ports they
  don't have; hide those in InputFlip.

## Tested

| Monitor | Cable | Switches away | Switches back | Brightness | Notes |
|---|---|---|---|---|---|
| **MSI G27CQ4** (FW.013, EDID `MSI 3CB0`) | HDMI | Yes | Yes, with a PS5 on | Yes | The reference monitor. Confirmed in about 0.15 s. The picture takes about 6 s to the PS5 and 5 s back, because the monitor briefly unplugs itself from the input it switches to (its own joystick does the same). No DDC/CI option in its menu; it's always on. Lists `11 12 0F 10` (has no DisplayPort 2). |
| MSI G27CQ4 | DisplayPort | Only with the screens off | Mostly | No | Ignores commands over DisplayPort while showing DisplayPort; garbled replies (`0xC0262589`). Works with InputFlip's "turn the screens off when leaving" setting, but the way back is unreliable. Use HDMI. |
| **Acer VG240Y** (EDID `ACR 0673`) | HDMI | Yes | Not tested | Yes | Answers every time. Lists `01 0F 11`; it has no DisplayPort port. |
| **Acer VG240Y** (EDID `ACR 06F0`) | HDMI, also through a DisplayPort-to-HDMI cable | Yes | Not tested | Yes | Answers through a passive-looking DP-to-HDMI cable with a converter chip. Capabilities string only says `model(acer)`. Lists `01 11 12`. |

Tested on Windows 11 with an NVIDIA RTX 4070 (driver 32.0.16.1714). On that PC, DDC/CI calls to different monitors
wait for each other (one queue in the driver), which is why InputFlip keeps its DDC/CI traffic small.

## Known patterns

These come from other projects and reports, not from tests here. Reports that confirm or correct them are welcome.

| Monitors | What's reported |
|---|---|
| Some **LG** models | Input commands must be sent to a different DDC/CI source address (`0x50` instead of `0x51`), which Windows' DDC/CI functions can't do. The switch is ignored. Tools that talk to the graphics card's I2C directly (NVIDIA NVAPI, AMD ADL) get around it. Not supported by InputFlip yet. |
| Some **Samsung** models | DDC/CI only over HDMI, or only after turning on an option in the menu. |
| Monitors with a **built-in KVM or USB-C hub** (some Dell, Gigabyte, MSI) | Usually fine, and some can also be controlled over USB. |
| **TVs** | Generally no DDC/CI. They use HDMI-CEC, which PC graphics cards don't send. |
| Behind **docks, USB-C hubs, KVM switches, MST hubs, active adapters** | DDC/CI often doesn't pass through. Test with the monitor plugged straight into the graphics card. |
| **DisplayPort** in general | Works on many monitors, but "works on HDMI, not on DisplayPort" is common, sometimes because of the graphics driver. Try HDMI first if switching fails. |

## Adding your monitor

Open InputFlip, then Settings › About › Diagnostics › Copy, and
[paste it into a monitor report](https://github.com/undah/inputflip/issues/new?template=monitor_report.yml). Say which
cable, which graphics card, and whether switching works both ways. A maintainer adds it to this table.
