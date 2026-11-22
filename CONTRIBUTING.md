# Contributing to InputFlip

Thanks for helping. The most useful things, roughly in order:

1. **Monitor reports.** Tried InputFlip on a monitor? [Tell us how it went](https://github.com/undah/inputflip/issues/new?template=monitor_report.yml),
   especially if it didn't work. Every monitor behaves a little differently, and the
   [compatibility list](docs/compatibility.md) is only as good as these reports.
2. **Bug reports** with the log and diagnostics (Settings › About › Open log, and Copy all under Diagnostics).
3. **Pull requests** for bugs, docs and the ideas in [docs/plan.md](docs/plan.md#later-ideas).

## Build and run

Windows 10 or 11 with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
(`winget install Microsoft.DotNet.SDK.10`). Visual Studio 2026 or Rider work, so does the command line:

```powershell
dotnet build InputFlip.slnx
dotnet test
dotnet run --project src/InputFlip
```

**Test without switching your monitors.** A real switch blanks your screen for a few seconds. While working on the UI:

```powershell
dotnet run --project src/InputFlip -- --data-dir $env:TEMP\if-test --no-switch
```

`--data-dir` uses its own settings folder (an empty one starts the first-run setup) and runs next to an installed copy.
`--no-switch` reads monitors but sends no commands at all (no switches, no brightness). Add `--open settings:scenes`
(or `general`, `monitors`, `rules`, `shortcuts`, `about`) or `--open setup` to land on a window.

## How the code is laid out

- `src/InputFlip.Core` has no UI: DDC/CI (`dxva2.dll`), monitor identity from EDID, settings, scenes, commands, USB
  rules. Everything with logic in it lives here and has unit tests in `tests/InputFlip.Core.Tests`.
- `src/InputFlip` is the WinUI 3 app (Windows App SDK 1.8, unpackaged). `AppController` owns the services and windows.
- [docs/architecture.md](docs/architecture.md) explains the parts; [CLAUDE.md](CLAUDE.md) has the rules the code
  follows and WinUI pitfalls we've hit.

## Rules the code follows

- Never call DDC/CI on the UI thread. One command queue per monitor, with retries and timeouts.
- A switch sends its command before doing anything else (window, mouse, log). Confirm it with a read-back; without
  one, say it's unconfirmed, never "done".
- Identify monitors by EDID model and serial, not by their order.
- Don't hard-code input values as the truth: read the capabilities string, fall back to the MCCS table, allow manual values.
- Text people see: sentence case, plain words, errors say what happened and what to do. No raw exception text.
- WinUI: switch Styles whose setters use `{ThemeResource}` instead of reading brushes in code (they wouldn't follow the
  window's theme); no `BrushTransition`/`ScalarTransition` on the input tiles (crashed on theme changes).

## Pull requests

- One change per pull request, with tests for anything in Core.
- `dotnet build InputFlip.slnx` with no warnings, and `dotnet test` passing.
- Say how you tested it, and on which monitors if it touches switching.
- Keep the docs in step: the README, the [user guide](docs/user-guide.md), and the
  [compatibility list](docs/compatibility.md) for monitor findings.

## Releases (maintainers)

Bump `<Version>` in `src/InputFlip/InputFlip.csproj`, add `docs/release-notes/v<version>.md`, then:

```powershell
dotnet tool install -g vpk --version 1.2.161   # once
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -Publish
```

It runs the tests, builds a self-contained Release, packs it with Velopack and creates the GitHub release; installed
copies then show an Update button.

## Code of conduct

Be kind; see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). By contributing you agree your contribution is released under
the [MIT licence](LICENSE).
