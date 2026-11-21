# Switchboard

A small Windows app that switches which input (HDMI, DisplayPort, USB-C) your monitor shows, with one click or a hotkey.

It talks to the monitor over **DDC/CI**, the control channel that runs over the display cable, using the standard "Input Source" command (VCP code `0x60`).

> "Switchboard" is a working name.

## Status

- **Stage 1** (proof of concept): done. [scripts/ddc-probe.ps1](scripts/ddc-probe.ps1) finds monitors, reads their inputs over DDC/CI and tests switching.
- **Stage 2** (working app): done; switching the MSI both ways is confirmed.
- **Stage 3** (polished v1): built. Windows 11 design, settings window, first-run setup, installer. Some checks need hands on the PC; see the list in [docs/plan.md](docs/plan.md#stage-3-polished-v1).

## Install

Build the installer (below) or take `SwitchboardApp-win-Setup.exe` from whoever built it, and run it. It needs nothing else: .NET and the Windows App SDK are inside. It isn't signed, so Windows SmartScreen warns the first time: click **More info**, then **Run anyway**.

It installs to `%LOCALAPPDATA%\SwitchboardApp` with Start menu and desktop shortcuts. Uninstall it from Settings › Apps; your settings stay in `%LOCALAPPDATA%\Switchboard`.

## Use it

Switchboard is a small window with its own taskbar button. The first time, it opens on a monitor other than your main one, with a short setup: the monitors it found and whether they answer, names and icons for your inputs, and shortcuts. Skip it if you like; everything is in Settings later. After that it opens wherever you last left it. Closing it quits the app.

- **Click an input** to switch to it. The input showing now has the accent color. **Right-click** an input (or Shift+F10) to rename it, pick an icon, hide it, or turn on the screens-off switch.
- The input this PC is plugged into says **This PC**. Monitors can't report which of their ports have something connected, so Switchboard works it out from the cable type Windows reports (HDMI, DisplayPort...). When two ports fit, it picks the one on screen the first time it looks. If it picked wrong, right-click the right input › *This PC is plugged in here*.
- **Keyboard:** Tab to the list, arrow keys between inputs, Enter to switch. F5 refreshes, Ctrl+, opens Settings.
- The **pin** keeps the window on top. The **gear** opens Settings:
  - **General:** start with Windows, light or dark, keep on top, edit the settings file.
  - **Monitors:** names, order, icons, which inputs show, add an input by its value, the screens-off switch.
  - **Shortcuts:** click a shortcut and press new keys. Switchboard says if another app or another Switchboard shortcut already has them.
  - **About:** version, log, run setup again, and each monitor's diagnostics (its capabilities string) to copy into a bug report.
- **Shortcuts** work while Switchboard runs, even minimized: `Ctrl+Alt+Shift+I` switches the main monitor to its next input; `Ctrl+Alt+Shift+S` brings the window to the front. Give any input its own shortcut in Settings › Shortcuts.

**Monitors that won't switch away from this PC** (like the MSI G27CQ4 over DisplayPort): in Settings › Monitors, set *Turn the screens off when switching away from* to this PC's input (setup suggests it when a monitor looks like that). Switching away from that input then turns all screens off for a few seconds, sends the command while no picture is on the cable, and turns them back on. Right after that, the monitor ignores commands for a few seconds (2 to 4 on the MSI), so a switch back in that time is re-sent every 2 seconds until it takes. Switch to the other device from Switchboard, not the monitor's own button: after a switch by hand, the MSI G27CQ4 ignored the way back.

## Build and run from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```powershell
winget install Microsoft.DotNet.SDK.10
```

Then, from the repo folder:

```powershell
dotnet run --project src/Switchboard
```

### Build the installer

```powershell
dotnet tool install -g vpk --version 1.2.161
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
```

It runs the tests, publishes a self-contained Release build to `publish\win-x64\` and packs it with [Velopack](https://velopack.io) into `releases\` (both git-ignored): `SwitchboardApp-win-Setup.exe` and a portable zip. The version comes from `<Version>` in `src/Switchboard/Switchboard.csproj`.

### Options for testing

| Option | What it does |
|---|---|
| `--data-dir <folder>` (or `SWITCHBOARD_DATA_DIR`) | Uses another folder for settings.json and the log. An empty folder starts the first-run setup. A copy with its own folder can run next to the normal one. |
| `--no-switch` | Never sends a switch command. Reads still work. |
| `--open settings[:general\|monitors\|shortcuts\|about]`, `--open setup` | Opens that window at start. |
| `--export-icon <file.ico>` | Writes the app icon and quits (to regenerate `src/Switchboard/Assets/Switchboard.ico`). |

### Settings file

`%LOCALAPPDATA%\Switchboard\settings.json`. Settings writes it; you can also edit it by hand (Settings › General › Edit), and changes apply when you save. Monitors show in the order they're listed.

```jsonc
{
  "cycleHotkey": "Ctrl+Alt+Shift+I",
  "cycleMonitor": "MSI3CB0-000000D1",        // which monitor the cycle shortcut switches; empty = main display
  "theme": "dark",                            // "light", "dark", or leave out to follow Windows
  "monitors": {
    "MSI3CB0-000000D1": {
      "nickname": "Main monitor",
      "blankScreensWhenLeaving": "0x0F",      // the screens-off switch: this PC's input
      "pcInput": "0x0F",                      // tagged "This PC"; found from the cable type and remembered
      "inputs": [
        { "value": "0x0F", "name": "Gaming PC", "icon": "pc" },
        { "value": "0x12", "name": "PS5", "icon": "console", "hotkey": "Ctrl+Alt+Shift+2" },
        { "value": "0x10", "hidden": true }    // a port the monitor lists but doesn't have
      ]
    }
  }
}
```

Icons: `pc`, `laptop`, `console`, `tv`, `display`. Input values are the DDC/CI values (see [docs/ddc-ci-notes.md](docs/ddc-ci-notes.md)). Start with Windows isn't in the file: it's the `Switchboard` entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, so Task Manager's Startup apps page sees it.

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
| [docs/ui-sketch.html](docs/ui-sketch.html) | Early clickable sketch from before stage 3 (open it in a browser). The app has moved on: a normal window rather than a tray flyout. |
| [CLAUDE.md](CLAUDE.md) | Context for Claude Code sessions working on this repo |

## Tech stack

- C# / .NET 10
- WinUI 3 (Windows App SDK 1.8, unpackaged, self-contained)
- Velopack for the installer
- CommunityToolkit.Mvvm
- Win32 `dxva2.dll` for DDC/CI, `RegisterHotKey` for global hotkeys

## Requirements

- Windows 10/11
- A monitor with DDC/CI turned on in its on-screen menu
