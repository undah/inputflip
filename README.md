# Switchboard

A small Windows tray app that switches which input (HDMI, DisplayPort, USB-C) your monitor shows, with one click or a hotkey.

It talks to the monitor over **DDC/CI**, the control channel that runs over the display cable, using the standard "Input Source" command (VCP code `0x60`).

> "Switchboard" is a working name.

## Status

Planning. No code yet. See [docs/plan.md](docs/plan.md) for the stage-by-stage plan and progress checklist.

## Docs

| File | What's in it |
|---|---|
| [docs/plan.md](docs/plan.md) | Stages 1–3 in detail, with a progress checklist |
| [docs/architecture.md](docs/architecture.md) | How the program is put together |
| [docs/ddc-ci-notes.md](docs/ddc-ci-notes.md) | DDC/CI background, input values, known problems and workarounds |
| [docs/ui-sketch.html](docs/ui-sketch.html) | Clickable sketch of the tray flyout and settings window (open it in a browser) |
| [CLAUDE.md](CLAUDE.md) | Context for Claude Code sessions working on this repo |

## Tech stack (planned)

- C# / .NET 8
- WinUI 3 (Windows App SDK)
- CommunityToolkit.Mvvm
- H.NotifyIcon (tray icon for WinUI 3)
- Win32 `dxva2.dll` for DDC/CI, `RegisterHotKey` for global hotkeys

## Requirements

- Windows 10/11
- A monitor with DDC/CI turned on in its on-screen menu
