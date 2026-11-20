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

- C# / .NET 8, WinUI 3 (Windows App SDK), CommunityToolkit.Mvvm, H.NotifyIcon.
- DDC/CI through `dxva2.dll` P/Invoke; hotkeys through `RegisterHotKey`.
- Settings in `%LOCALAPPDATA%\Switchboard\settings.json`.
- Stage 1 probe is a PowerShell script (`scripts/ddc-probe.ps1`) using `Add-Type` so it runs with no build step.

## Rules for the code

- Never call DDC/CI on the UI thread. One serial command queue per physical monitor; retries and timeouts on every call.
- Identify monitors by EDID model + serial, not enumeration order.
- Don't hard-code input values as the truth: read the capabilities string, fall back to the MCCS table, allow manual values.
- User-facing text: sentence case, plain language, errors say what happened and what to do. No raw exception text in the UI.
- Release physical monitor handles (`DestroyPhysicalMonitors`).

## Environment

- Windows 11. Shell commands may run in PowerShell or Git Bash; check which before using shell-specific syntax.
- Testing anything DDC/CI-related needs the owner's real monitor; ask them to run it and report results rather than guessing.
