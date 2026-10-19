# WinResume

Windows doesn't bring back your working session after a restart. Only apps that register for it reopen, and
VS Code, Sublime, Discord, terminals, Explorer tabs and most other apps don't. WinResume is a small tray app that
saves what is open **only at the moment you shut down, restart or sign out**, and offers to put it back when you
sign in again.

> **Status: early prototype.** It works on the author's machine and has been tested through a real restart, but it
> is not a finished product. Expect rough edges (see [Known gaps](#known-gaps)).

## Install

**Requirements:** Windows 10 or 11, 64-bit. Nothing else. There is no installer, no administrator prompt and no
.NET to install.

1. Go to the [**Releases**](https://github.com/jiteshm17/WinResume/releases) page and download
   `WinResume-<version>-win-x64.zip` from the latest release.
2. Unzip it anywhere you want to keep it (for example `C:\Users\<you>\Apps\WinResume`). It is a single file,
   `WinResume.App.exe`.
3. Double-click `WinResume.App.exe`. **SmartScreen may warn** that the app is unrecognised, because the file is not
   code-signed yet. Click **More info**, then **Run anyway**.
4. Look for the WinResume icon in the system tray (bottom right, possibly behind the `^`). Right-click it and tick
   **Start with Windows**, so it is running next time you shut down.

That's it. From then on, shut down or restart as usual. At the next sign-in WinResume waits about 15 seconds for
your other apps to start, then asks whether to restore your session.

> Don't move or delete the exe afterwards without untick-and-retick of "Start with Windows", because Windows
> remembers the path.

### Optional: verify the download
Each release has a `.sha256` file. In PowerShell, `Get-FileHash WinResume-<version>-win-x64.zip` should print the
same value.

### Uninstall
1. Right-click the tray icon, untick **Start with Windows**, then **Exit**.
2. Delete the exe.
3. Optionally delete the data folder `%LocalAppData%\WinResume` (the saved session, settings and a small log).

### Updating
Download the newer zip, exit WinResume from the tray menu, and replace the exe.

## What it brings back

| What | How |
|---|---|
| Any ordinary desktop app | relaunched with the same arguments and window position |
| Chrome / Edge | each open profile reopens, with its own tabs (the browser restores them) |
| Word, Excel, PowerPoint | the exact documents that were open |
| File Explorer | every window with all of its tabs and folders |
| Store apps | launched by package id |
| Windows Terminal | tabs in their folders with Python virtual environments activated; scrollback is restored by Windows Terminal itself |

Apps that Windows already starts for you at sign-in (the Startup apps list, including the Task Manager on/off
switch) are left alone, as are apps that are already running.

**Tip:** turn off *Settings > Accounts > Sign-in options > "Automatically save my restartable apps and restart them
when I sign back in"*. Windows' own restore overlaps with this app for Chrome and Explorer.

## The tray menu

| Item | What it does |
|---|---|
| Restore last session now | restores the saved session without waiting for sign-in |
| Pause (save nothing at shutdown) | stops saving anything |
| Include Windows Terminal tabs | on by default; untick to leave terminals alone |
| Start with Windows | adds or removes the per-user startup entry |
| Open data folder | opens `%LocalAppData%\WinResume` |
| Exit | quits the app |

## Privacy

- Nothing is captured while you work. The only trigger is Windows telling the app the session is ending.
- One file, `%LocalAppData%\WinResume\last-session.json`, holds the latest session. It has no window titles and is
  deleted once restored (or if you decline, or if the shutdown is cancelled).
- Nothing is sent anywhere. There is no network code. The source is all here to check.
- **Pause** in the tray menu saves nothing at all.
- The saved session does contain the command lines apps were started with, folder names and document paths, so
  treat that file like any other file in your user profile.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Windows 10/11.

```
git clone https://github.com/jiteshm17/WinResume.git
cd WinResume
dotnet build
dotnet test
dotnet run --project src/WinResume.App
```

To produce the same single file as the releases:

```
dotnet publish src/WinResume.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`src/WinResume.Spike` is a command-line tool used for development (`snapshot`, `restore`, `terminal-scan`, ...).
`scripts/` has the test scripts, including `kill-and-restore.ps1` (close apps, restore, compare) and
`baseline-before-restart.ps1` / `verify-after-restart.ps1` for testing a real restart.

## Releases

Releases are built by GitHub Actions from the tagged source, so the zip is not built on anyone's laptop. Pushing a tag
such as `v0.1.0` builds, tests and publishes a release with the zip and its checksum. Until there is a stable
version, releases are marked **pre-release**.

## How it works

- `WinResume.Core/Capture.cs` lists the open windows and works out how each app should be reopened. Ordinary apps use
  one generic path; Explorer, Chromium browsers, Office and Store apps have small dedicated handlers.
- `WinResume.Core/Terminal/` is a separate module for Windows Terminal. Windows Terminal already saves and
  restores its own tabs and scrollback but loses each tab's folder and activated environment, so WinResume fills
  in those two fields in Windows Terminal's saved layout before it starts.
- `WinResume.App` has an invisible window that receives the shutdown message (`WM_QUERYENDSESSION`).

## Known gaps

- Running programs are not restarted (a tab's last command is put in history so the up arrow recalls it).
- Windows Terminal tabs running plain PowerShell report their start folder; an optional prompt hook
  (`scripts/terminal-prompt-hook.ps1`) gives the exact one.
- Reading Explorer's tabs briefly flicks through them at shutdown, and under a real shutdown it can run out of
  time (Windows gives apps only a few seconds).
- Apps that start themselves minimised to the tray (such as WhatsApp or Wispr Flow) come back in the tray, not as a
  visible window.
- Apps running as administrator can't be inspected or restored.
- Unsigned build: SmartScreen warns on first run.

## Licence

[MIT](LICENSE)
