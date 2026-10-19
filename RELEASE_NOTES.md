**Early prototype.** It brings back your open apps after a restart, but it has only been tested on one machine.
Read the [README](https://github.com/jiteshm17/WinResume#readme) before installing, especially "Known gaps".

### Install
1. Download `WinResume-<version>-win-x64.zip` below and unzip it anywhere you like.
2. Run `WinResume.App.exe`. Windows may show a SmartScreen warning because the file is not code-signed: click
   **More info**, then **Run anyway**.
3. Find the tray icon (bottom right, maybe under the `^`), right-click it and tick **Start with Windows**.

No installer, no administrator rights, and no .NET install are needed.

### Verify the download (optional)
Compare the SHA-256 of the zip with the `.sha256` file attached to this release:
`Get-FileHash WinResume-<version>-win-x64.zip`
