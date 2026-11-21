# CLAUDE.md

Context for Claude Code sessions working on this repo. Several Claude accounts work on it, so **this repo is the only shared memory**: anything worth knowing next time goes into a file here, not just into the conversation.

## Project

Switchboard: a small Windows app that switches a monitor's input (HDMI / DisplayPort / USB-C) over DDC/CI, VCP code `0x60`. Working name.

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
- **A normal app window, not a tray app** (owner's choice, 2026-11-20): taskbar button, opens where it was last left (first time: a monitor other than the main one), close quits. The title bar is our header (`ExtendsContentIntoTitleBar`). A hidden Win32 window (`src/Switchboard/Native/HostWindow.cs`) handles hotkeys, display changes and resume.
- Settings is a second window (`SettingsWindow`, NavigationView with General / Monitors / Shortcuts / About); first-run setup is a third (`SetupWindow`), shown when settings.json doesn't exist. Every change applies and saves at once, through `AppController.Change`.
- The app icon is drawn in code (`Core/Branding/IconArt.cs`) and written to `src/Switchboard/Assets/Switchboard.ico` (`Switchboard.exe --export-icon <path>`); a unit test fails if the two drift apart.
- Installer: **Velopack** (`scripts/build-installer.ps1`, vpk 1.2.161 to match the `Velopack` package). Pack id `SwitchboardApp`, not `Switchboard`: Velopack deletes its install folder on uninstall, and `%LOCALAPPDATA%\Switchboard` holds the settings. Unsigned; releases aren't published (owner's call). No auto-update yet: the repo is private, so there's no update URL friends could reach.
- Start with Windows is the `Switchboard` value under `HKCU\...\CurrentVersion\Run`, not a settings.json field.
- DDC/CI through `dxva2.dll` P/Invoke; hotkeys through `RegisterHotKey`.
- Monitor identity from the EDID Windows caches in the registry (`HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\...\Device Parameters\EDID`), not WMI.
- Settings in `%LOCALAPPDATA%\Switchboard\settings.json`, log in `switchboard.log` next to it. `--data-dir <folder>` (or `SWITCHBOARD_DATA_DIR`) uses another folder; such a copy runs alongside the normal one.
- Default hotkeys avoid `Ctrl+Alt+letter`/digit (AltGr on many layouts): `Ctrl+Alt+Shift+I` cycles, `Ctrl+Alt+Shift+S` brings the window to the front.
- Stage 1 probe is a PowerShell script (`scripts/ddc-probe.ps1`) using `Add-Type` so it runs with no build step.

## Code layout

- `src/Switchboard.Core`: no UI. DDC/CI interop and the per-monitor queue (`Monitors/`), cable type from `QueryDisplayConfig` (`Interop/DisplayConnections.cs`), capabilities and EDID parsing, settings and the edits the settings window makes (`Settings/SettingsEditor.cs`), setup suggestions (`Settings/SetupAdvisor.cs`), start with Windows (`Settings/StartupRegistration.cs`), hotkey parsing and shortcut slots (`Hotkeys/`), command-line options, the icon drawing. Unit-tested.
- `src/Switchboard`: the WinUI app. `Program.cs` is `Main` (Velopack hooks, options); `AppController` owns the services, the view models and the windows; `MainWindow`, `SettingsWindow`, `SetupWindow`; `Pages/` are the settings pages; `Controls/` the settings card, input list editor and shortcut recorder; `Styles.xaml` the shared styles; `Native/` the Win32 host window and `WindowChrome` (title bar, Mica, theme, placement).
- `tests/Switchboard.Core.Tests`: xUnit. `dotnet test` from the repo root.
- The app project turns off the framework/runtime folders in its output path, because the XAML compiler fails on paths over 260 characters when the repo sits deep in the file system.
- `EnableMsixTooling` is on although nothing is MSIX: without it `dotnet publish` leaves out the compiled XAML and the published app fails with "XAML parsing failed".
- Build the whole thing with `dotnet build Switchboard.slnx` (x64) or `dotnet build src/Switchboard`.

## WinUI pitfalls found here

- **Don't resolve theme brushes in code** (`Application.Current.Resources["...Brush"]` from an x:Bind function): you get one theme's brush regardless of the window's theme. Switch Styles whose setters use `{ThemeResource}` instead (see `Ui.RowStyle`).
- **No `KeyboardAccelerator` for keys without a `VirtualKey` name** (Ctrl+, is `0xBC`): WinUI 1.8 crashes natively when it builds the tooltip text. Handle the key in a KeyDown handler.
- **No `BrushTransition`/`ScalarTransition` on the input rows:** the app crashed (access violation) on a theme change with them.
- `AppWindow.ResizeClient` leaves out the title bar when content extends into it; `MainWindow.Resize` sizes from `XamlRoot.Size` instead.
- Caption button colors: `AppWindow.TitleBar.PreferredTheme`, not hand-set `Button*Color`s (those make the disabled maximize button look enabled).
- A native XAML crash leaves no managed stack: the event log shows `0xc000027b`. Bisect by commenting out XAML.

## Rules for the code

- Never call DDC/CI on the UI thread. One serial command queue per physical monitor; retries and timeouts on every call.
- Identify monitors by EDID model + serial, not enumeration order.
- Don't hard-code input values as the truth: read the capabilities string, fall back to the MCCS table, allow manual values.
- User-facing text: sentence case, plain language, errors say what happened and what to do. No raw exception text in the UI.
- Release physical monitor handles (`DestroyPhysicalMonitor`). A handle can be `0`; track "has a handle" separately.
- Never report a switch as done from the `SetVCPFeature` result alone; confirm with a read-back, otherwise say it's unconfirmed.
- Don't test by clicking inputs or pressing switch hotkeys unless the owner is at the PC: it really switches their monitors. To test the UI safely, start a copy with `--data-dir <temp folder> --no-switch` (add `--open settings:monitors` or `--open setup` to land on a window). It still reads the real monitors.

## Environment

- Windows 11. Shell commands may run in PowerShell or Git Bash; check which before using shell-specific syntax.
- Testing anything DDC/CI-related needs the owner's real monitor; ask them to run it and report results rather than guessing.
