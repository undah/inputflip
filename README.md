# Switchboard

A small Windows app that switches which input (HDMI, DisplayPort, USB-C) your monitor shows, with one click or a hotkey.

It talks to the monitor over **DDC/CI**, the control channel that runs over the display cable, using the standard "Input Source" command (VCP code `0x60`).

> "Switchboard" is a working name.

## Status

- **Stage 1** (proof of concept): almost done. [scripts/ddc-probe.ps1](scripts/ddc-probe.ps1) finds monitors, reads their inputs over DDC/CI and tests switching.
- **Stage 2** (working app): built, waiting for hands-on testing. A small window that lists your monitors and switches inputs with a click or a hotkey.

See [docs/plan.md](docs/plan.md) for the stage-by-stage plan and progress checklist.

## Run it

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```powershell
winget install Microsoft.DotNet.SDK.10
```

Then, from the repo folder:

```powershell
dotnet run --project src/Switchboard
```

Switchboard opens as a small window with its own taskbar button. The first time, it opens on a monitor other than your main one; after that, wherever you last left it. Closing it quits the app.

- **Click an input** to switch to it. **Right-click** an input to rename it, pick an icon or hide it.
- The **pin** button keeps the window on top of other windows.
- The **…** button has Edit settings file and Open log.
- **Shortcuts** (work while Switchboard is running, even minimized): `Ctrl+Alt+Shift+I` switches the main monitor to its next input; `Ctrl+Alt+Shift+S` brings the window to the front.

### Settings file

`%LOCALAPPDATA%\Switchboard\settings.json`. It's filled in automatically; edit it for anything the window can't do yet. Changes apply when you save.

```jsonc
{
  "cycleHotkey": "Ctrl+Alt+Shift+I",
  "cycleMonitor": "MSI3CB0-000000D1",        // which monitor the cycle shortcut switches; empty = main display
  "monitors": {
    "MSI3CB0-000000D1": {
      "nickname": "Main monitor",
      "inputs": [
        { "value": "0x11", "name": "Gaming PC", "icon": "pc" },
        { "value": "0x12", "name": "PS5", "icon": "console", "hotkey": "Ctrl+Alt+Shift+2" },
        { "value": "0x10", "hidden": true }    // a port the monitor lists but doesn't have
      ]
    }
  }
}
```

Icons: `pc`, `laptop`, `console`, `tv`, `display`.

**Monitors that won't switch away from this PC** (like the MSI G27CQ4 over DisplayPort): right-click your PC's input and choose *Turn screens off when leaving this input*, or set `"blankScreensWhenLeaving": "0x0F"` on the monitor. Switching away from that input then turns all screens off for a few seconds, sends the command while no picture is on the cable, and turns them back on. Input values are the DDC/CI values (see [docs/ddc-ci-notes.md](docs/ddc-ci-notes.md)).

## Test

```powershell
dotnet test
```

## Docs

| File | What's in it |
|---|---|
| [docs/plan.md](docs/plan.md) | Stages 1–3 in detail, with a progress checklist |
| [docs/architecture.md](docs/architecture.md) | How the program is put together |
| [docs/ddc-ci-notes.md](docs/ddc-ci-notes.md) | DDC/CI background, input values, known problems and workarounds |
| [docs/ui-sketch.html](docs/ui-sketch.html) | Early clickable sketch of the switch window and settings window (open it in a browser). The app is now a normal window rather than a tray flyout. |
| [CLAUDE.md](CLAUDE.md) | Context for Claude Code sessions working on this repo |

## Tech stack

- C# / .NET 10
- WinUI 3 (Windows App SDK 1.8, unpackaged)
- CommunityToolkit.Mvvm
- Win32 `dxva2.dll` for DDC/CI, `RegisterHotKey` for global hotkeys

## Requirements

- Windows 10/11
- A monitor with DDC/CI turned on in its on-screen menu
