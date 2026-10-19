# Run this after you have restarted, signed in and answered the restore prompt (give it a minute to settle).
# Read-only: it takes a new snapshot and compares it with the baseline from baseline-before-restart.ps1.
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
$root = Split-Path $PSScriptRoot -Parent
$spike = "$root\src\WinResume.Spike\bin\Debug\net8.0-windows\WinResume.Spike.exe"
$dir = Join-Path $env:LOCALAPPDATA 'WinResume\test'
. "$PSScriptRoot\verify-common.ps1"

if (-not (Test-Path "$dir\baseline.json")) { throw 'No baseline found. Run baseline-before-restart.ps1 before restarting.' }

Write-Host '== what the app logged'
$log = Join-Path $env:LOCALAPPDATA 'WinResume\log.txt'
if (Test-Path $log) { Get-Content $log -Tail 40 | ForEach-Object { '   ' + $_ } } else { Write-Host '   (no log file)' }

& $spike snapshot "$dir\after.json" --terminal | Out-Null
$before = Get-Content "$dir\baseline.json" -Raw | ConvertFrom-Json
$after  = Get-Content "$dir\after.json" -Raw | ConvertFrom-Json
$fail = 0
function Report($ok, $text) { if ($ok) { Write-Host "  PASS  $text" } else { Write-Host "  FAIL  $text"; $script:fail++ } }

Write-Host "`n== apps and windows (the terminal app itself is covered by its tabs below)"
$a = @($after.Apps | ForEach-Object { Key $_ })
foreach ($item in $before.Apps) {
    $k = Key $item
    $back = $k -in $a
    # a plain app counts as back if its process is running, even when it starts hidden in the tray
    if (-not $back -and $k -like 'Win32: *') { $back = [bool](Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($k.Substring(7))) -ErrorAction SilentlyContinue) }
    Report $back $k
}

Write-Host "`n== Chrome tabs"
$tabsBefore = @(Get-Content "$dir\baseline-chrome-tabs.txt")
$tabsAfter = @(ChromeTabs)
$lost = @($tabsBefore | Where-Object { $_ -notin $tabsAfter })
Report ($lost.Count -eq 0) ("Chrome tabs: {0} before, {1} after" -f $tabsBefore.Count, $tabsAfter.Count)
$lost | ForEach-Object { Write-Host "        missing: $_" }

Write-Host "`n== terminal tabs (folder and Python environment)"
$tb = @($before.Terminal.Tabs); $ta = @($after.Terminal.Tabs)
Report ($tb.Count -eq $ta.Count) ("terminal tab count: {0} before, {1} after" -f $tb.Count, $ta.Count)
for ($i = 0; $i -lt [Math]::Min($tb.Count, $ta.Count); $i++) {
    $same = ($tb[$i].Directory -eq $ta[$i].Directory) -and ($tb[$i].EnvKind -eq $ta[$i].EnvKind) -and ($tb[$i].EnvPath -eq $ta[$i].EnvPath)
    Report $same ("tab {0}: {1} [{2}]" -f $i, $ta[$i].Directory, $ta[$i].EnvKind)
}

Write-Host "`n== duplicates (the same app opened twice)"
$dups = @($after.Apps | Where-Object { $_.Kind -eq 'Explorer' }).Count
$wantExplorer = @($before.Apps | Where-Object { $_.Kind -eq 'Explorer' }).Count
Report ($dups -eq $wantExplorer) "Explorer windows: $wantExplorer before, $dups after"
$wtWindows = @($after.Terminal.Tabs | Group-Object WindowId).Count
$wtWantedWindows = @($before.Terminal.Tabs | Group-Object WindowId).Count
Report ($wtWindows -eq $wtWantedWindows) "Windows Terminal windows: $wtWantedWindows before, $wtWindows after"

Write-Host ""
if ($fail -eq 0) { Write-Host 'Everything that was open came back.' } else { Write-Host "$fail check(s) failed. The log above shows what the app did." }
exit $fail
