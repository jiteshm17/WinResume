# OPTIONAL. Not installed automatically.
#
# PowerShell's `cd` does not change the directory Windows reports for the shell process, so from the
# outside a tab that you moved to C:\work\proj still looks like it is in your home folder. Without this
# hook WinResume falls back to a best guess (the folder of a program the tab is running, or the project
# folder that contains an activated .venv). With it, the exact folder is restored.
#
# What it does: each time the prompt is shown it writes the CURRENT FOLDER PATH ONLY (no commands, no
# output) to %LOCALAPPDATA%\WinResume\terminal-tabs\<tab id>.txt, overwriting the previous value.
# Nothing leaves your machine. Delete that folder at any time.
#
# To use it, add the contents of this file to the end of your $PROFILE and restart your tabs.

$global:__WinResumeOriginalPrompt = $function:prompt
function global:prompt {
    if ($env:WT_SESSION -and $PWD.Provider.Name -eq 'FileSystem') {
        try {
            $dir = Join-Path $env:LOCALAPPDATA 'WinResume\terminal-tabs'
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Set-Content -LiteralPath (Join-Path $dir (($env:WT_SESSION).Trim('{', '}') + '.txt')) -Value $PWD.Path -NoNewline
        } catch { }
    }
    & $global:__WinResumeOriginalPrompt
}
