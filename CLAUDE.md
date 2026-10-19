# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

WinResume is a Windows 10/11 tray app (C#, .NET 8, WinForms) that saves the user's open apps **only when Windows is
shutting down, restarting or signing out**, and offers to restore them at the next sign-in. The README covers install,
privacy and the user-facing behaviour; this file covers how to work in the code.

## Git workflow

- Never commit to `main`. Do the work on a branch so the user can raise a pull request later.
- If already on a non-`main` branch and the requested work belongs with that branch, stay on it. Otherwise create a new
  branch, or ask the user whether to stash or discard uncommitted changes first.
- Push the branch, but do not open or merge the pull request unless asked.
- Commit messages end with the attribution line the harness gives you.

## Commands

```
dotnet build                                   # whole solution (Core, App, Spike, Tests)
dotnet test                                    # all unit tests (pure logic, no apps are touched)
dotnet test --filter "FullyQualifiedName~DirectoryChooserTests"        # one class
dotnet test --filter "FullyQualifiedName~DirectoryChooserTests.Hook_beats_everything"   # one test
dotnet run --project src/WinResume.App         # the tray app
dotnet run --project src/WinResume.Spike -- snapshot out.json [--terminal] [--skip-explorer]
dotnet run --project src/WinResume.Spike -- restore out.json [--only a.exe,b.exe | --skip a.exe] [--terminal]
```

Spike also has `terminal-scan`, `terminal-restore [--dry-run]`, `terminal-patch [--apply]`, `close-chrome`, `quit-office`.
There is no linter configured.

Gotchas that cost time:
- `dotnet test` does **not** rebuild `WinResume.App` or `WinResume.Spike`. Run `dotnet build` before using Spike or the
  scripts, or they run stale Core code.
- A running `WinResume.App.exe` locks its own exe. Stop it before rebuilding, and start it again afterwards.
- The solution targets `net8.0-windows`; everything here is Windows-only.

## Architecture (the parts that span several files)

**Capture happens only at the end of a session.** `WinResume.App` has an invisible window (`ShutdownWindow` in
`TrayApp.cs`) that handles `WM_QUERYENDSESSION`, registers a shutdown block reason, and calls
`SessionFlow.CaptureForShutdown`. That writes one `last-session.json` via `SessionStore`. There is deliberately no
periodic or background capture; do not add one. At the next start, `TrayApp` finds the file, waits ~15 s, asks, and calls
`SessionFlow.RestoreSession`, which deletes the file when done.

**Threading rule.** Reading processes (WMI) and other apps' windows (UI Automation) fails on the UI/STA thread.
Everything that captures or restores must run on an MTA background thread (`CaptureForShutdown` and the restore `Task.Run`
already do). A call that works from the console Spike can still fail inside the tray app for this reason.

**Capture and restore are one generic path plus a few families.** `Capture.TakeSnapshot` enumerates visible top-level
windows and emits one `AppEntry` per process. Ordinary apps (exe, command line, window placement) are the default and
carry no per-app logic. Only these families have dedicated handling, selected by `AppKind`:
Explorer (`ExplorerTabs.cs`), Chromium (`Chromium.cs`, profiles come from the browser's `Local State`), Office
(`Office.cs`, documents via COM), and Packaged/Store apps (launched by AUMID). `Restore.RestoreOne` switches on `AppKind`.
Adding an app family means a new `AppKind`, a capture branch and a restore case.

**Restore skips what will come back by itself.** `Restore.ShouldSkip` skips plain/Store apps that are already running or
that Windows starts at sign-in (`StartupApps`, which reads the Run keys, Startup folders, Store startup tasks and the
Task Manager on/off flags). Browsers, Explorer and Office are never skipped because they open content into a running
instance.

**Windows Terminal is an isolated module** (`WinResume.Core/Terminal/`, behind `Settings.RestoreTerminal` and the
`--terminal` flag). Windows Terminal already restores its own tabs and scrollback from `state.json`; the module only
fills in what that file loses. `TerminalScanner` reads each shell's folder and `VIRTUAL_ENV`/conda variables out of
process memory (`ProcessMemory`), `TerminalStatePatcher` writes `startingDirectory`/`commandline` into the saved layout
(with a backup, and only while Windows Terminal is not running), then the app launches `wt.exe`. Keep it separable from
the app-restore code so it can be tested alone.

**Snapshot model.** `Snapshot` / `AppEntry` (JSON, enums as strings) is the contract between capture, storage and
restore. `SessionStore.Folder` is a static that tests redirect to a temp folder, which is why `SessionStoreTests` is in a
non-parallel xUnit collection.

## Testing

- Unit tests cover pure logic: command parsing, directory choice, startup matching, patching, session storage. Add them
  alongside any new logic. Providers that drive real apps are tested live, not in unit tests.
- Live scripts in `scripts/`: `kill-and-restore.ps1` (close the test apps, restore, compare), and for a real restart
  `baseline-before-restart.ps1` then `verify-after-restart.ps1`. Compare results against what was open before; do not
  report success from the tool's own log alone.
- The simulated shutdown is `WinResume.App.exe --simulate-shutdown` (needs the tray app running); `--restore-now` runs the
  sign-in restore without the prompt. Neither actually shuts anything down.
- Troubleshooting paths (log, saved session, state files) are in the README.

### Safety rules when testing on the user's real machine

- Never close VS Code (it hosts the Claude Code session), Windows Terminal while the user has live work in it, or
  Wispr Flow, unless told to. Do not touch Word/Excel/PowerPoint documents with unsaved changes; quit Office only through
  `Office.QuitIfAllSaved`.
- `claude.exe` is both Claude Desktop and the Claude Code CLI: close only the one under `AnthropicClaude`, and never the
  test script's own process tree.
- Close Chrome gracefully (`Chromium.CloseGracefully`); never force-kill it.
- Explorer windows: close with UI Automation (`WindowPattern.Close`), not `WM_CLOSE`, and watch the window count. If more
  windows appear than expected, stop the test. Never type text into other windows; the only key ever sent is one Enter,
  after confirming Explorer is the foreground window.
- Ask before anything that ends the user's work (closing terminals with running programs, restarting the machine).

## Conventions the project owner asked for

- Privacy first: nothing recorded while the user works, no window titles saved, no network code, one latest session file.
  No encryption layer (the source is open and inspectable); do not add complexity for it.
- Keep it simple; prefer the generic path over per-app special cases.
- Only claim a result after comparing against the original state, and say plainly what was and was not verified.
