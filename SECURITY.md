# Security

## Reporting a problem

Please don't open a public issue for a security problem. Use GitHub's private reporting instead:
[Report a vulnerability](https://github.com/undah/inputflip/security/advisories/new). You'll get an answer within a
week. Once there's a fix, the advisory is published with credit to you, unless you'd rather not.

## Supported versions

Only the latest release gets fixes. The Update button in InputFlip installs it.

## What InputFlip can do on your PC

So you can judge the risk:

- It sends DDC/CI commands to your monitors through Windows (`dxva2.dll`): input, brightness, contrast, volume.
- It registers global keyboard shortcuts (`RegisterHotKey`), and can move the mouse pointer when a monitor switches.
- It writes to your user's registry only: the start-with-Windows entry (HKCU `Run`) and the `inputflip://` link
  handler (HKCU `Software\Classes\inputflip`). Both are removed on uninstall.
- It listens on a named pipe for commands from a second start of InputFlip. The pipe accepts connections from your
  Windows user only.
- It reads its settings from `%LOCALAPPDATA%\InputFlip` and writes a log there.
- It contacts the internet only to check for updates (GitHub releases, or the address in `updateSource`). No telemetry.
- It doesn't need admin rights and installs per user.

The installer isn't code-signed yet, so Windows SmartScreen warns the first time. Download it only from this repository's
[releases page](https://github.com/undah/inputflip/releases).
