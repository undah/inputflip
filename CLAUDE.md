# CLAUDE.md

Context for Claude Code sessions working on this repo. Several Claude accounts work on it, so **this repo is the only shared memory**: anything worth knowing next time goes into a file here, not just into the conversation.

## Project

Switchboard: a Windows tray app that switches a monitor's input (HDMI / DisplayPort / USB-C) over DDC/CI, VCP code `0x60`. Working name.

- Plan and progress checklist: `docs/plan.md`
- Architecture: `docs/architecture.md`
- DDC/CI background, input values, switch-back workarounds: `docs/ddc-ci-notes.md`
- UI sketch: `docs/ui-sketch.html`

## Where things stand

Check `docs/plan.md`: ticked boxes are done, the "Results" table in stage 1 holds what we've learned about the owner's monitor. Start every session by reading it and `git log --oneline -15`.

## Working agreements (multiple accounts)

1. **Pull before you start:** `git pull --rebase`.
2. **Work on a branch** named for the task (e.g. `stage1-probe-script`), not on `main`.
3. **Update `docs/plan.md` in the same commit** as the work: tick boxes, fill in the results table.
4. **Record decisions and findings** in the relevant doc (e.g. monitor quirks go in `docs/ddc-ci-notes.md` or the stage 1 results table). If a decision changes the plan, edit the plan.
5. **Push when you stop,** even if unfinished, and say what's left in the commit message or a PR description.
6. Keep commits small; one logical change each.

## Tech decisions (so far)

- C# / **.NET 10** (LTS). Not .NET 8: its support ended on 2026-11-10. Build needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).
- WinUI 3 on **Windows App SDK 1.8**, unpackaged and self-contained (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`): no MSIX, no separate runtime install. 2.x exists but this code was written against 1.8; upgrade deliberately.
- CommunityToolkit.Mvvm 8.4, used without its source generators (plain `ObservableObject.SetProperty`).
- **Tray icon is our own Win32 code** (`src/Switchboard/Native`), not H.NotifyIcon: one hidden window handles tray clicks, hotkeys, display changes and resume, and the icon is drawn in code to match the taskbar theme.
- DDC/CI through `dxva2.dll` P/Invoke; hotkeys through `RegisterHotKey`.
- Monitor identity from the EDID Windows caches in the registry (`HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\...\Device Parameters\EDID`), not WMI.
- Settings in `%LOCALAPPDATA%\Switchboard\settings.json`, log in `switchboard.log` next to it.
- Default hotkeys avoid `Ctrl+Alt+letter`/digit (AltGr on many layouts): `Ctrl+Alt+Shift+I` cycles, `Ctrl+Alt+Shift+S` opens the window.
- Stage 1 probe is a PowerShell script (`scripts/ddc-probe.ps1`) using `Add-Type` so it runs with no build step.

## Code layout

- `src/Switchboard.Core`: no UI. DDC/CI interop and the per-monitor queue (`Monitors/`), capabilities and EDID parsing, settings, hotkey parsing, input list logic (`Settings/InputLayout.cs`). Unit-tested.
- `src/Switchboard`: the WinUI app. `AppController` owns the services and the view model; `FlyoutWindow` is the switch window; `Native/` is the Win32 host window, tray icon and icon drawing.
- `tests/Switchboard.Core.Tests`: xUnit. `dotnet test` from the repo root.
- The app project turns off the framework/runtime folders in its output path, because the XAML compiler fails on paths over 260 characters when the repo sits deep in the file system.

## Rules for the code

- Never call DDC/CI on the UI thread. One serial command queue per physical monitor; retries and timeouts on every call.
- Identify monitors by EDID model + serial, not enumeration order.
- Don't hard-code input values as the truth: read the capabilities string, fall back to the MCCS table, allow manual values.
- User-facing text: sentence case, plain language, errors say what happened and what to do. No raw exception text in the UI.
- Release physical monitor handles (`DestroyPhysicalMonitor`). A handle can be `0`; track "has a handle" separately.
- Never report a switch as done from the `SetVCPFeature` result alone; confirm with a read-back, otherwise say it's unconfirmed.
- Don't test by clicking inputs or pressing switch hotkeys unless the owner is at the PC: it really switches their monitors.

## Environment

- Windows 11. Shell commands may run in PowerShell or Git Bash; check which before using shell-specific syntax.
- Testing anything DDC/CI-related needs the owner's real monitor; ask them to run it and report results rather than guessing.
