# Switchboard

A small Windows tray app that switches which input (HDMI, DisplayPort, USB-C) your monitor shows, with one click or a hotkey.

It talks to the monitor over **DDC/CI**, the control channel that runs over the display cable, using the standard "Input Source" command (VCP code `0x60`).

> "Switchboard" is a working name.

## Status

- **Stage 1** (proof of concept): almost done. [scripts/ddc-probe.ps1](scripts/ddc-probe.ps1) finds monitors, reads their inputs over DDC/CI and tests switching.
- **Stage 2** (working app): built, waiting for hands-on testing. A tray app that lists your monitors and switches inputs with a click or a hotkey.

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

Switchboard goes to the notification area (the tray). On the first run it also opens its window.

- **Click an input** to switch to it. **Right-click** an input to rename it, pick an icon or hide it.
- The **pin** button keeps the window open on top. Drag it by its header, for example onto a monitor you aren't switching.
- **Shortcuts:** `Ctrl+Alt+Shift+I` switches the main monitor to its next input; `Ctrl+Alt+Shift+S` opens the window.
- **Right-click the tray icon** for Refresh, Edit settings file, Open log and Quit.

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

Icons: `pc`, `laptop`, `console`, `tv`, `display`. Input values are the DDC/CI values (see [docs/ddc-ci-notes.md](docs/ddc-ci-notes.md)).

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
| [docs/ui-sketch.html](docs/ui-sketch.html) | Clickable sketch of the tray flyout and settings window (open it in a browser) |
| [CLAUDE.md](CLAUDE.md) | Context for Claude Code sessions working on this repo |

## Tech stack

- C# / .NET 10
- WinUI 3 (Windows App SDK 1.8, unpackaged)
- CommunityToolkit.Mvvm
- Win32 `dxva2.dll` for DDC/CI, `RegisterHotKey` for global hotkeys, `Shell_NotifyIcon` for the tray icon

## Requirements

- Windows 10/11
- A monitor with DDC/CI turned on in its on-screen menu
