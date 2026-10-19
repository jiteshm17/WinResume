# Shared helpers for the baseline and verify scripts. Read-only: only looks at windows.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public class VW {
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

# Names of the tabs in every Chrome window (the browser's own tab strip only, not buttons inside web pages).
function ChromeTabs {
    $names = @()
    foreach ($h in [VW]::ByClass('Chrome_WidgetWin_1', 'chrome')) {
        $e = [Windows.Automation.AutomationElement]::FromHandle($h)
        $strip = $e.FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Tab))) |
            Where-Object { $_.Current.ClassName -eq 'TabContainerImpl' } | Select-Object -First 1
        if (-not $strip) { continue }
        foreach ($t in $strip.FindAll('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::TabItem)))) {
            $names += ($t.Current.Name -replace ' - Memory usage - .*$', '' -replace '^\s+|\s+$', '')
        }
    }
    $names | Sort-Object
}

# One comparable line per item in a snapshot.
function Key($a) {
    switch ($a.Kind) {
        'Explorer' { 'Explorer: ' + ($a.ExplorerTabs -join ' | ') }
        'Chromium' { 'Chromium profile: ' + $a.ChromiumProfile }
        'Packaged' { 'Packaged: ' + $a.Aumid }
        'Office'   { 'Office ' + (Split-Path $a.ExePath -Leaf) + ': ' + (($a.Documents | Sort-Object) -join ' | ') }
        default    { 'Win32: ' + (Split-Path $a.ExePath -Leaf) }
    }
}
