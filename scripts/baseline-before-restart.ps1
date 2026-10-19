# Run this right before you restart. It records what is open now (read-only) so verify-after-restart.ps1 can
# compare against it afterwards. It does not close or change anything. Stored in %LocalAppData%\WinResume\test.
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
$root = Split-Path $PSScriptRoot -Parent
$spike = "$root\src\WinResume.Spike\bin\Debug\net8.0-windows\WinResume.Spike.exe"
$dir = Join-Path $env:LOCALAPPDATA 'WinResume\test'; New-Item -ItemType Directory -Force $dir | Out-Null
. "$PSScriptRoot\verify-common.ps1"

& $spike snapshot "$dir\baseline.json" --terminal | Out-Null
$chrome = @(ChromeTabs)
$chrome | Set-Content "$dir\baseline-chrome-tabs.txt"
Copy-Item "$env:LOCALAPPDATA\WinResume\last-session.json" "$dir\baseline-session-file.json" -ErrorAction SilentlyContinue

$snap = Get-Content "$dir\baseline.json" -Raw | ConvertFrom-Json
Write-Host "Baseline saved to $dir"
Write-Host ("  apps/windows : {0}" -f @($snap.Apps).Count)
Write-Host ("  terminal tabs: {0}" -f @($snap.Terminal.Tabs).Count)
Write-Host ("  chrome tabs  : {0}" -f $chrome.Count)
