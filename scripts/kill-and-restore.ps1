# Kill-and-restore test: snapshot -> close the test apps -> restore -> snapshot again -> compare.
#
# Safety rules:
#  - Chrome is closed gracefully (every window closed like the user would) and never force-killed.
#  - Word/Excel/PowerPoint are quit through COM only when every open document is saved; otherwise they are left running.
#  - File Explorer: windows only (never explorer.exe the shell). Wispr Flow, VS Code, Windows Terminal are never touched.
#  - This script's own process tree is never closed (the Claude Code CLI is also named claude.exe).
param(
    [string[]]$CloseExes = @('claude', 'WhatsApp.Root', 'Discord', 'sublime_text'),   # simple apps: graceful, then force
    [switch]$SkipClose
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
$root = Split-Path $PSScriptRoot -Parent
$tmp  = Join-Path $env:TEMP 'winresume-test'; New-Item -ItemType Directory -Force $tmp | Out-Null
$before = Join-Path $tmp 'before.json'; $after = Join-Path $tmp 'after.json'
$spike = "$root\src\WinResume.Spike\bin\Debug\net8.0-windows\WinResume.Spike.exe"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

dotnet build "$root\src\WinResume.Spike" -nologo -v q | Out-Null
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public class W {
  public delegate bool CB(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(CB c, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static System.Collections.Generic.List<IntPtr> ByClass(string cls, string proc) {
    var r = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, l) => { var s = new StringBuilder(64); GetClassName(h, s, 64);
      if (s.ToString() == cls && IsWindowVisible(h)) {
        if (proc != null) { uint pid; GetWindowThreadProcessId(h, out pid);
          try { if (System.Diagnostics.Process.GetProcessById((int)pid).ProcessName != proc) return true; } catch { return true; } }
        r.Add(h); }
      return true; }, IntPtr.Zero);
    return r;
  }
}
"@
function CabinetHandles { @([W]::ByClass('CabinetWClass', $null)) }

# Names of the tabs in each Chrome window, e.g. to prove "same tabs came back".
function ChromeTabs {
    $names = @()
    foreach ($h in [W]::ByClass('Chrome_WidgetWin_1', 'chrome')) {
        $e = [Windows.Automation.AutomationElement]::FromHandle($h)
        # The browser's own tab strip is the Tab control with class TabContainerImpl; other Tab controls
        # are buttons and chips inside the web page (e.g. YouTube's category bar) and must be ignored.
        $strip = $e.FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Tab))) |
            Where-Object { $_.Current.ClassName -eq 'TabContainerImpl' } | Select-Object -First 1
        if (-not $strip) { continue }
        foreach ($t in $strip.FindAll('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::TabItem)))) {
            $names += ($t.Current.Name -replace ' - Memory usage - .*$', '' -replace '^\s+|\s+$', '')
        }
    }
    $names | Sort-Object
}

Write-Host '== snapshot (before)'
& $spike snapshot $before | Out-Host
$snapBefore = Get-Content $before -Raw | ConvertFrom-Json
$tabsBefore = @(ChromeTabs)
Write-Host "   chrome tabs before: $($tabsBefore.Count)"

$restoreSkip = 'Code.exe,WindowsTerminal.exe,Wispr Flow.exe'
if (-not $SkipClose) {
    Write-Host '== closing apps'
    # Office: only when everything is saved
    foreach ($o in @($snapBefore.Apps | Where-Object Kind -eq 'Office')) {
        & $spike quit-office $o.ExePath | Out-Host
        if ($LASTEXITCODE -ne 0) { $restoreSkip += ',' + (Split-Path $o.ExePath -Leaf); Write-Host "   (leaving $(Split-Path $o.ExePath -Leaf) running and out of the test)" }
    }
    # Chrome: graceful only
    & $spike close-chrome chrome | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Chrome did not exit on its own; stopping without force-killing it' }
    # Explorer windows: WM_CLOSE is unreliable, UI Automation Close() works. Repeat until none are left.
    for ($i = 0; $i -lt 10; $i++) {
        $cabinets = CabinetHandles
        if ($cabinets.Count -eq 0) { break }
        foreach ($h in $cabinets) {
            try { [Windows.Automation.AutomationElement]::FromHandle($h).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() } catch {}
            Start-Sleep -Milliseconds 500
        }
        Start-Sleep -Seconds 1
    }
    if ((CabinetHandles).Count -gt 0) { throw 'Explorer windows would not close; aborting before restore so nothing is duplicated' }

    # Simple apps. Never close this script's own process tree; Claude Desktop only (the CLI is also claude.exe).
    $protect = @{}; $cur = $PID
    while ($cur) { $protect[[int]$cur] = $true; $cur = (Get-CimInstance Win32_Process -Filter "ProcessId=$cur" -ErrorAction SilentlyContinue).ParentProcessId; if ($cur -eq 0) { break } }
    function Targets($n) { Get-Process -Name $n -ErrorAction SilentlyContinue | Where-Object { -not $protect.ContainsKey($_.Id) -and ($n -ne 'claude' -or $_.Path -like '*\AnthropicClaude\*') } }
    foreach ($n in $CloseExes) { Targets $n | ForEach-Object { $_.CloseMainWindow() | Out-Null } }
    Start-Sleep -Seconds 6
    foreach ($n in $CloseExes) { Targets $n | Stop-Process -Force }
    Start-Sleep -Seconds 2

    Write-Host '== restore'
    & $spike restore $before --skip $restoreSkip | Out-Host
    Write-Host '== waiting for apps to settle'
    Start-Sleep -Seconds 20
}

Write-Host '== snapshot (after)'
& $spike snapshot $after | Out-Host
$snapAfter = Get-Content $after -Raw | ConvertFrom-Json
$tabsAfter = @(ChromeTabs)

function Key($a) {
    switch ($a.Kind) {
        'Explorer' { 'Explorer: ' + ($a.ExplorerTabs -join ' | ') }
        'Chromium' { 'Chromium profile: ' + $a.ChromiumProfile }
        'Packaged' { 'Packaged: ' + $a.Aumid }
        'Office'   { 'Office ' + (Split-Path $a.ExePath -Leaf) + ': ' + (($a.Documents | Sort-Object) -join ' | ') }
        default    { 'Win32: ' + (Split-Path $a.ExePath -Leaf) }
    }
}
$b = @($snapBefore.Apps | ForEach-Object { Key $_ }); $a = @($snapAfter.Apps | ForEach-Object { Key $_ })
$missing = @($b | Where-Object { $_ -notin $a })
# A plain app counts as back if its process is running again, even when it starts hidden in the tray
$missing = @($missing | Where-Object { -not ($_ -like 'Win32: *' -and (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($_.Substring(7))) -ErrorAction SilentlyContinue)) })

Write-Host "`n== result"
foreach ($k in $b) { Write-Host ("  {0}  {1}" -f ($(if ($k -notin $missing) { 'PASS' } else { 'FAIL' })), $k) }

$wantExplorer = @($snapBefore.Apps | Where-Object Kind -eq 'Explorer').Count
$gotExplorer  = @($snapAfter.Apps  | Where-Object Kind -eq 'Explorer').Count
if ($gotExplorer -ne $wantExplorer) { Write-Host "  FAIL  Explorer window count: wanted $wantExplorer, got $gotExplorer"; $missing += 'explorer window count' }

$lostTabs  = @($tabsBefore | Where-Object { $_ -notin $tabsAfter })
$extraTabs = @($tabsAfter  | Where-Object { $_ -notin $tabsBefore })
Write-Host ("  {0}  Chrome tabs: {1} before, {2} after" -f ($(if ($lostTabs.Count -eq 0) { 'PASS' } else { 'FAIL' })), $tabsBefore.Count, $tabsAfter.Count)
foreach ($t in $lostTabs)  { Write-Host "        missing: $t" }
if ($extraTabs.Count -gt 0) { Write-Host "        new tabs after restore: $($extraTabs.Count) (e.g. $($extraTabs[0]))" }
if ($lostTabs.Count -gt 0) { $missing += 'chrome tabs' }

if ($missing.Count -gt 0) { Write-Host "`n$($missing.Count) item(s) did not come back"; exit 1 } else { Write-Host "`nAll items came back"; exit 0 }
