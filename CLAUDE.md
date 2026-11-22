# CLAUDE.md

Context for Claude Code sessions working on this repo. Several Claude accounts work on it, so **this repo is the only shared memory**: anything worth knowing next time goes into a file here, not just into the conversation.

## Project

InputFlip (called Switchboard until 1.1): a Windows app that switches a monitor's input (HDMI / DisplayPort / USB-C) over DDC/CI, VCP code `0x60`. Open source (MIT), public at https://github.com/undah/inputflip since 2026-11-22.

- Plan and history: `docs/plan.md`
- Architecture: `docs/architecture.md`
- User guide: `docs/user-guide.md`; command line and links: `docs/command-line.md`; monitors tested: `docs/compatibility.md`
- DDC/CI background, input values, switch-back workarounds: `docs/ddc-ci-notes.md`
- Release notes: `docs/release-notes/v<version>.md`

## Where things stand

Check `docs/plan.md`: ticked boxes are done, the "Results" table in stage 1 holds what we've learned about the owner's monitor, stage 4 is InputFlip (features, look, open source, 1.1.0) and lists what still needs the owner at the PC. Start every session by reading it and `git log --oneline -15`.

## Working agreements (multiple accounts)

1. **Pull before you start:** `git pull --rebase`.
2. **The owner commits straight to `main`** (their call). Outside contributors use pull requests.
3. **Update `docs/plan.md` in the same commit** as the work: tick boxes, fill in the results table.
4. **Record decisions and findings** in the relevant doc (monitor quirks in `docs/ddc-ci-notes.md` and `docs/compatibility.md`). If a decision changes the plan, edit the plan.
5. **Push when you stop,** even if unfinished, and say what's left in the commit message. If it doesn't build yet, push it to a side branch instead of `main`.
6. Keep commits small; one logical change each.
7. **The repo is public.** Commits use GitHub's noreply address (`112290109+undah@users.noreply.github.com`, set in this repo's git config); the history was rewritten to it on 2026-11-22. Don't commit personal paths, emails or anything secret.

## Tech decisions (so far)

- C# / **.NET 10** (LTS). Build needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).
- WinUI 3 on **Windows App SDK 1.8**, unpackaged and self-contained (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`): no MSIX, no separate runtime install. 2.x exists but this code was written against 1.8; upgrade deliberately.
- CommunityToolkit.Mvvm 8.4, used without its source generators (plain `ObservableObject.SetProperty`).
- **A normal app window, plus a tray icon** (owner's choices, 2026-11-20 and 2026-11-21): taskbar button, opens where it was last left (first time: a monitor other than the main one), close quits unless "close to the tray" is on. The title bar is our header (`ExtendsContentIntoTitleBar`). A hidden Win32 window (`src/InputFlip/Native/HostWindow.cs`) handles hotkeys, display changes, sleep/wake, lock/unlock, USB notices and the tray icon.
- **Look: "Tiles" in clean blue** (owner picked Tiles from three mockups on 2026-11-21, then asked for blue instead of coral on 2026-11-22): inputs as big tiles, the current one `#2563EB` with white text, on `#10141B` (dark) or `#F7F9FC` (light). The blue also overrides `SystemAccentColor*` so standard controls match. No Mica. Colors live in `Styles.xaml` theme dictionaries (with a high-contrast one).
- The logo in the UI is vector (`Controls/LogoMark.xaml`). The `.ico` (`Assets/InputFlip.ico`) is drawn in code by `Core/Branding/IconArt.cs` (`InputFlip.exe --export-icon <path>`); a unit test fails if the two drift apart. Keep LogoMark's geometry in step with IconArt.
- Settings is a second window (General / Monitors / Scenes / Rules / Shortcuts / About); first-run setup is a third (`SetupWindow`), shown when settings.json doesn't exist. Every change applies and saves at once, through `AppController.Change`.
- Added in 1.1 (owner picked all eight, 2026-11-21): scenes, command line and `inputflip://` links, USB rules, pop-up picker and toast, brightness/contrast, speaker volume, tray icon, lock/sleep rules.
- Installer: **Velopack** (`scripts/build-installer.ps1`, vpk 1.2.161 to match the `Velopack` package). The pack id stays **`SwitchboardApp`** so 1.0 installs keep updating (Velopack only updates the same pack id); the main exe is `InputFlip.exe`. Downloads are copied to `InputFlip-Setup.exe` / `InputFlip-Portable.zip`. Unsigned. The 1.0.7 → 1.1.0 update on the owner's PC renamed and re-pointed the shortcuts by itself (checked 2026-11-22).
- **Updates come from GitHub releases** (owner's choice, 2026-11-22; before that, this PC's `releases\` folder). `Updater.DefaultSource` is the repo; settings.json `updateSource` overrides it (a folder or URL); `build-installer.ps1 -LocalUpdates` bakes this PC's `releases\` in, to try an update before publishing. **To ship:** bump `<Version>` in `src/InputFlip/InputFlip.csproj`, add `docs/release-notes/v<version>.md`, build without `-Publish`, start `publish\win-x64\InputFlip.exe --data-dir <temp> --no-switch` once to see it runs, then `build-installer.ps1 -Publish` (or `gh release create` with the files in `releases\` except the two `SwitchboardApp-win-*` copies).
- Start with Windows is the `InputFlip` value under `HKCU\...\CurrentVersion\Run` (a 1.0 `Switchboard` value is moved at start). `inputflip://` links under `HKCU\Software\Classes\inputflip`. Both removed on uninstall.
- Commands from a second start (`--switch`, links) go to the running copy over a named pipe (`App.CommandPipeName`, current user only).
- DDC/CI through `dxva2.dll` P/Invoke; hotkeys through `RegisterHotKey`.
- Monitor identity from the EDID Windows caches in the registry (`HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\...\Device Parameters\EDID`), not WMI.
- Settings in `%LOCALAPPDATA%\InputFlip\settings.json`, log `inputflip.log` next to it; **PCs that ran 1.0 keep `%LOCALAPPDATA%\Switchboard`** (`AppPaths.PickDefault`), the owner's included (its old `switchboard.log` stays there too). `--data-dir <folder>` (or `INPUTFLIP_DATA_DIR`) uses another folder; such a copy runs alongside the normal one.
- Capabilities strings are cached in `capabilities.json` next to settings.json (read once per monitor; About › Read again). **DDC/CI calls to different monitors block each other on the owner's PC** (an MSI read waited 2.6 s behind an Acer's capabilities read), so keep DDC/CI traffic to what's needed: it delays switches on every monitor. Slider values go through `LatestValueSender`.
- Default hotkeys avoid `Ctrl+Alt+letter`/digit (AltGr on many layouts): `Ctrl+Alt+Shift+I` cycles, `Ctrl+Alt+Shift+P` picker, `Ctrl+Alt+Shift+S` brings the window to the front. A cleared built-in shortcut is saved as `""` so it doesn't come back as the default.
- Stage 1 probe is a PowerShell script (`scripts/ddc-probe.ps1`) using `Add-Type` so it runs with no build step.

## Code layout

- `src/InputFlip.Core`: no UI. DDC/CI interop and the per-monitor queue (`Monitors/`), cable type from `QueryDisplayConfig` (`Interop/DisplayConnections.cs`), USB device list (`Interop/UsbDevices.cs`), capabilities and EDID parsing, settings and the edits the settings window makes (`Settings/SettingsEditor.cs`), setup suggestions (`Settings/SetupAdvisor.cs`), which input is this PC's (`Settings/PcInputDetector.cs`), start with Windows and links (`Settings/StartupRegistration.cs`, `LinkRegistration.cs`), scenes, rule actions, commands and picker items (`Actions/`), USB rules (`Usb/`), hotkey parsing and shortcut slots (`Hotkeys/`), the icon drawing. Unit-tested.
- `src/InputFlip`: the WinUI app. `Program.cs` is `Main` (Velopack hooks, options, the command client); `AppController` (+ `AppController.Features.cs`) owns the services, the view models and the windows; `MainWindow`, `SettingsWindow`, `SetupWindow`, `PickerWindow`, `ToastWindow`; `Pages/` are the settings pages; `Controls/` the settings card, input list editor, shortcut recorder and logo; `Styles.xaml` the shared styles and palette; `Motion.cs` the tile flip; `Native/` the Win32 host window, the command pipe and `WindowChrome` (title bar, theme, placement).
- `tests/InputFlip.Core.Tests`: xUnit. `dotnet test` from the repo root.
- `.github/`: issue forms, PR template, and a build-and-test workflow on every push and pull request.
- The app project turns off the framework/runtime folders in its output path, because the XAML compiler fails on paths over 260 characters when the repo sits deep in the file system.
- `EnableMsixTooling` is on although nothing is MSIX: without it `dotnet publish` leaves out the compiled XAML and the published app fails with "XAML parsing failed".
- Build the whole thing with `dotnet build InputFlip.slnx` (x64) or `dotnet build src/InputFlip`.
- Namespaces are `InputFlip.*` since 2026-11-22 (were `Switchboard.*`).

## WinUI pitfalls found here

- **Don't resolve theme brushes in code** (`Application.Current.Resources["...Brush"]` from an x:Bind function): you get one theme's brush regardless of the window's theme. Switch Styles whose setters use `{ThemeResource}` instead (see `Ui.TileStyle`, `Ui.TileIconStyle`).
- **No `KeyboardAccelerator` for keys without a `VirtualKey` name** (Ctrl+, is `0xBC`): WinUI 1.8 crashes natively when it builds the tooltip text. Handle the key in a KeyDown handler.
- **No `BrushTransition`/`ScalarTransition` on the input tiles:** the app crashed (access violation) on a theme change with them. Animate with Composition on the element's visual instead (`Motion.cs`).
- `x:Load` needs an `x:Name` on the same element.
- An `Image` showing an `.ico` is blurry at sizes above the frame WinUI picks; draw logos as shapes.
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
- A switch sends its command before doing anything else (window, mouse, toast, log). The window shows the new input at once and corrects itself if the read-back disagrees.
- Don't test by clicking inputs or pressing switch hotkeys unless the owner is at the PC: it really switches their monitors. To test the UI safely, start a copy with `--data-dir <temp folder> --no-switch` (add `--open settings:scenes` or `--open setup` to land on a window); `--no-switch` blocks brightness and volume too. It still reads the real monitors. Stop only that copy (match its command line), never every `InputFlip.exe`: the owner's installed copy has the same name.

## Environment

- Windows 11. Shell commands may run in PowerShell or Git Bash; check which before using shell-specific syntax. In Git Bash, inline `sed`/`perl` with backslashes gets mangled; write a script file and run it instead. In Windows PowerShell 5.1, `Out-File -Encoding utf8` adds a byte-order mark, which ends up in a commit message written that way; use a Git Bash heredoc with `git commit -F -`.
- PowerShell variable names ignore case: a `[switch]$Publish` parameter and a `$publish` variable are the same variable.
- Testing anything DDC/CI-related needs the owner's real monitor; ask them to run it and report results rather than guessing.
- **The Claude desktop app is an MSIX package, and so is everything it starts.** Shell commands and any InputFlip started from a Claude session get `%LOCALAPPDATA%` redirected to `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\`. So a copy started from a session saves its settings *there*, and the installed app never sees them; reading `%LOCALAPPDATA%\...` from a session may show the sandboxed copy. To read the real folder, go around the redirection: `\\localhost\C$\Users\<user>\AppData\Local\Switchboard`. Test copies should use `--data-dir` anyway.
- **The owner's hardware (2026-11-21):** RTX 4070 with one HDMI port and three DisplayPort ports. The MSI G27CQ4 is on the HDMI port at 1440p 144 Hz, PC on its HDMI 2 (`0x12`), PS5 on HDMI 1 (`0x11`). Windows lays out: Acer `0673` left, MSI (main) middle, Acer `06F0` right. **The MSI unplugs itself from whichever input it switches to**, with the joystick too: about 6 s to the PS5 picture, and on the way back Windows loses the main display for 0.8 s and every screen goes black for about 5 s. DDC/CI can't change that (docs/ddc-ci-notes.md); don't chase it in the app.
- **Screens-off tests (`scripts/quiet-link-test.ps1`) go in Windows PowerShell from the Start menu, not the Claude terminal panel.** From the Claude terminal the MSI's screens-off switch failed 5 rounds in 7, from Windows PowerShell 0 in 8, with the same timings. Cause unknown. The script writes `logs\quiet-link-test.log` in the repo, so ask the owner to run it and read the log.
