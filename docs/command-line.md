# Command line, links and Stream Deck

Everything InputFlip does with a click or a shortcut also works from a script, a desktop shortcut, AutoHotkey, a
Stream Deck button or a link. Commands go to the copy of InputFlip that's already running, so they take effect at
once. If InputFlip isn't running, the command starts it and runs once it has found the monitors.

## Commands

```powershell
InputFlip.exe --switch <monitor> <input>     # switch a monitor to an input
InputFlip.exe --switch <input>               # the same, on the main monitor
InputFlip.exe --scene <scene>                # run a scene (again: go back)
InputFlip.exe --toggle [monitor]             # next input, like the next-input shortcut
InputFlip.exe --brightness [monitor] <level> # 0 to 100, or a step such as +10 or -10
InputFlip.exe --contrast [monitor] <level>
InputFlip.exe --volume [monitor] <level>     # the monitor's own speakers
InputFlip.exe --mute [monitor]
InputFlip.exe --unmute [monitor]
InputFlip.exe --picker                       # open the pop-up picker by the mouse
InputFlip.exe --show                         # bring the window to the front
InputFlip.exe --list                         # monitors, inputs and scenes
```

The installer puts `InputFlip.exe` in `%LOCALAPPDATA%\SwitchboardApp\current\`. The Start menu shortcut points there.

### Naming a monitor

Any of these, in this order:

- `main` (or leave it out): the main display.
- Its number in InputFlip's window, from the top: `1`, `2`, `3`. `--list` shows the numbers.
- Its id, as `--list` and Settings › About show it: `MSI3CB0-000000D1`. It never changes, even if you rename the
  monitor or plug it into another port.
- Its name in InputFlip, or its model name: `"Left Acer"`, `"MSI G27CQ4"`. Case and spaces don't matter.
- Part of a name, if only one monitor matches: `MSI`, `acer`.
- For brightness, contrast and volume: `all` (every shown monitor that has the control).

### Naming an input

- Your name for it: `PS5`, `"Work laptop"`.
- The port: `"HDMI 1"`, `hdmi1`, `DisplayPort2`, `USB-C`.
- `pc`: the input this PC is plugged into.
- The DDC/CI value: `0x11`, `17` or `11h` (see the [input values](ddc-ci-notes.md#standard-input-values-mccs)). A value
  works even if the monitor doesn't list it.
- Part of a name, if only one input matches.

### Answers and exit codes

Started from a console, InputFlip prints one line saying what happened, and sets the exit code:

| Exit code | Meaning |
|---|---|
| 0 | Done (for a switch: the monitor confirmed it, or didn't answer the read-back but took the command) |
| 1 | It didn't work: unknown monitor or input, the monitor didn't respond or didn't switch |
| 2 | A mistake in the command itself, such as `--brightness 150` |

```text
> InputFlip.exe --switch msi xbox
MSI G27CQ4 has no input called "xbox". It has Gaming PC, PS5.
```

PowerShell doesn't wait for Windows apps: pipe the output to see it in order, `InputFlip.exe --list | Out-String`.

## Links: `inputflip://`

The installed app registers `inputflip://` links, so the same commands work wherever a web address does:

| Link | Same as |
|---|---|
| `inputflip://switch/MSI/PS5` | `--switch MSI PS5` |
| `inputflip://switch/HDMI%202` | `--switch "HDMI 2"` (spaces as `%20`) |
| `inputflip://scene/Console` | `--scene Console` |
| `inputflip://toggle` | `--toggle` |
| `inputflip://brightness/all/-10` | `--brightness all -10` |
| `inputflip://mute/MSI` | `--mute MSI` |
| `inputflip://picker` | `--picker` |

The first time a browser opens one, it asks whether to open InputFlip.

## Stream Deck

Two ways, both with built-in actions:

- **Website** action (System › Website): URL `inputflip://scene/Console`. Untick "GET request in background".
- **Open** action (System › Open): App / File `"%LOCALAPPDATA%\SwitchboardApp\current\InputFlip.exe" --scene Console`.

For a dial (Stream Deck +): one Website action per direction, `inputflip://brightness/all/+10` and `.../-10`.

## Desktop shortcut

Right-click the desktop › New › Shortcut, and as the location:

```text
"%LOCALAPPDATA%\SwitchboardApp\current\InputFlip.exe" --switch MSI PS5
```

Give the shortcut a key in its Properties (Shortcut key) if you like, though InputFlip's own shortcuts
(Settings › Shortcuts) react faster.

## AutoHotkey

```autohotkey
; AutoHotkey v2: Win+F1 to the console, Win+F2 back to the PC
InputFlip := EnvGet("LOCALAPPDATA") "\SwitchboardApp\current\InputFlip.exe"
#F1::Run '"' InputFlip '" --switch MSI PS5'
#F2::Run '"' InputFlip '" --switch MSI pc'
```

## Batch and PowerShell scripts

```powershell
# Switch the main monitor to the laptop, and say so if it didn't work
$exe = "$env:LOCALAPPDATA\SwitchboardApp\current\InputFlip.exe"
$answer = & $exe --switch "Work laptop" | Out-String
if ($LASTEXITCODE -ne 0) { Write-Warning $answer }
```

## Testing options

These are for trying InputFlip out, or working on it:

| Option | What it does |
|---|---|
| `--data-dir <folder>` (or `INPUTFLIP_DATA_DIR`) | Uses another folder for settings.json and the log. An empty folder starts the first-run setup. A copy with its own folder runs next to the normal one, and commands go to the copy with the same folder. |
| `--no-switch` | Never sends a command to a monitor: no switches, brightness or volume. Reads still work. |
| `--open settings[:general\|monitors\|scenes\|rules\|shortcuts\|about]`, `--open setup` | Opens that window at start. |
| `--export-icon <file.ico>` | Writes the app icon and quits. |
