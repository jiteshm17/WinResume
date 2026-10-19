using System.Windows.Automation;
using static WinResume.Core.Native;

namespace WinResume.Core;

/// <summary>
/// File Explorer (Windows 11) exposes only the active tab of a window through Shell.Application,
/// so tabs are captured by selecting each tab with UI Automation and reading the address bar.
/// Restore sends no keystrokes: it opens the first folder as a window, then for each further tab
/// clicks the window's own "Add New Tab" button and navigates that tab through Shell.Application.
/// </summary>
public static class ExplorerTabs
{
    public static bool IsExplorerWindow(IntPtr hwnd) => ClassOf(hwnd) == "CabinetWClass";

    // ---- finding the real tabs ----------------------------------------

    /// <summary>
    /// The window's own tabs. Other TabItem elements exist (sidebar entries on the Home page),
    /// so only the list items inside the TabView count.
    /// </summary>
    static List<AutomationElement> RealTabs(AutomationElement root)
    {
        var view = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "TabView"));
        if (view is null) return new();
        return view.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
            .Cast<AutomationElement>()
            .Where(t => t.Current.ClassName == "ListViewItem")
            .ToList();
    }

    static int TabCount(IntPtr hwnd)
    {
        try { return RealTabs(AutomationElement.FromHandle(hwnd)).Count; }
        catch { return 0; }
    }

    // ---- capture ------------------------------------------------------

    /// <summary>Returns tab paths in order plus the index of the originally active tab.</summary>
    public static (List<string> Paths, int Active) Capture(IntPtr hwnd)
    {
        var paths = new List<string>();
        int active = 0;
        AutomationElement root;
        try { root = AutomationElement.FromHandle(hwnd); } catch { return (paths, 0); }

        var tabs = RealTabs(root);
        if (tabs.Count == 0)
        {
            var only = ActivePath(hwnd);
            if (only is not null) paths.Add(only);
            return (paths, 0);
        }

        for (int i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && p is SelectionItemPattern sel
                && sel.Current.IsSelected) active = i;
        }
        // The address bar only shows the real path while it has focus, so each tab is
        // selected, the address bar focused and read, then focus returns to the tab item.
        var addressBar = FindAddressBar(root); // looked up once: searching the window again for every tab is slow
        for (int i = 0; i < tabs.Count; i++)
        {
            if (!tabs[i].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p)) continue;
            ((SelectionItemPattern)p).Select();
            Thread.Sleep(SettleAfterSelect);
            var value = ReadAddressBar(addressBar);
            try { tabs[i].SetFocus(); } catch { }
            if (value is not null) paths.Add(NormalizeLocation(value));
        }
        // put the user's original tab back in front
        if (tabs[active].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var back))
            ((SelectionItemPattern)back).Select();
        return (paths, Math.Min(active, Math.Max(0, paths.Count - 1)));
    }

    /// <summary>Pauses (ms) after selecting a tab and after focusing the address bar, so the address has updated before it is read.</summary>
    internal static int SettleAfterSelect = 150, SettleAfterFocus = 120;

    static AutomationElement? FindAddressBar(AutomationElement root) =>
        root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, "Address Bar"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));

    static string? ReadAddressBar(AutomationElement? edit)
    {
        if (edit is null) return null;
        try
        {
            edit.SetFocus();
            Thread.Sleep(SettleAfterFocus);
            return ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
        }
        catch { return null; }
    }

    static readonly Dictionary<string, string> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Desktop"] = "shell:Desktop",
        ["Downloads"] = "shell:Downloads",
        ["Documents"] = "shell:Personal",
        ["Pictures"] = "shell:My Pictures",
        ["Music"] = "shell:My Music",
        ["Videos"] = "shell:My Video",
        ["This PC"] = "shell:MyComputerFolder",
        ["Home"] = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}",
        ["Network"] = "shell:NetworkPlacesFolder",
        ["Recycle Bin"] = "shell:RecycleBinFolder",
    };

    /// <summary>The address bar shows only a display name for known folders; turn those into shell: names.</summary>
    public static string NormalizeLocation(string value)
    {
        var v = value.Trim();
        return KnownFolders.TryGetValue(v, out var shell) ? shell : v;
    }

    /// <summary>Path of the window's active tab via Shell.Application.</summary>
    static string? ActivePath(IntPtr hwnd)
    {
        var w = ShellWindow(hwnd);
        try { return w is null ? null : (string)w.Document.Folder.Self.Path; }
        catch { return null; }
    }

    static dynamic? ShellWindow(IntPtr hwnd)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return null;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            foreach (var w in shell.Windows())
            {
                try { if ((long)w.HWND == hwnd.ToInt64()) return w; }
                catch { /* non-Explorer shell window */ }
            }
        }
        catch { }
        return null;
    }

    // ---- restore ------------------------------------------------------

    /// <summary>Most Explorer windows a single restore may create before it stops itself.</summary>
    const int MaxNewWindows = 1;

    /// <summary>Opens the saved tabs in one new window. Returns false if it had to stop early.</summary>
    public static bool Restore(AppEntry app, Action<string>? log = null)
    {
        var paths = app.ExplorerTabs;
        if (paths is null || paths.Count == 0) return true;

        var before = CabinetWindows();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{paths[0]}\"") { UseShellExecute = true });

        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 50 && hwnd == IntPtr.Zero; i++)
        {
            Thread.Sleep(200);
            var added = CabinetWindows().Except(before).ToList();
            if (added.Count > MaxNewWindows) { log?.Invoke($"explorer: {added.Count} new windows appeared, stopping"); return false; }
            hwnd = added.FirstOrDefault();
        }
        if (hwnd == IntPtr.Zero) { log?.Invoke("explorer: window did not appear"); return false; }

        for (int i = 1; i < paths.Count; i++)
        {
            if (!AddTabAndNavigate(hwnd, paths[i], log)) { log?.Invoke($"explorer: could not open tab {i + 1}, stopping"); break; }
            if (CabinetWindows().Except(before).Count() > MaxNewWindows) { log?.Invoke("explorer: too many windows, stopping"); return false; }
        }
        SelectTab(hwnd, Math.Min(app.ExplorerActiveTab, TabCount(hwnd) - 1));
        log?.Invoke($"explorer: {TabCount(hwnd)} of {paths.Count} tab(s) restored");
        return TabCount(hwnd) == paths.Count;
    }

    static bool AddTabAndNavigate(IntPtr hwnd, string path, Action<string>? log)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var count = TabCount(hwnd);
        var add = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "AddButton"));
        if (add is null || !add.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) return false;
        ((InvokePattern)p).Invoke();

        for (int w = 0; w < 20 && TabCount(hwnd) <= count; w++) Thread.Sleep(150);
        if (TabCount(hwnd) <= count) return false;
        Thread.Sleep(400); // the new tab becomes the active one

        // Shell.Application can only navigate a window's first tab, so the address bar is used.
        // The path is set through UI Automation (no typing); the only key sent is a single Enter,
        // and only after confirming Explorer is the foreground window.
        var edit = root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, "Address Bar"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        if (edit is null) return false;
        try
        {
            if (!BringToForeground(hwnd)) { log?.Invoke("explorer: could not bring window to front, not sending keys"); return false; }
            edit.SetFocus();
            Thread.Sleep(250);
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(path);
            Thread.Sleep(250);
            if (GetForegroundWindow() != hwnd) { log?.Invoke("explorer: window lost focus, not sending Enter"); return false; }
            keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch (Exception e) { log?.Invoke($"explorer: navigate failed: {e.Message}"); return false; }
        Thread.Sleep(900);
        return true;
    }

    static bool BringToForeground(IntPtr hwnd)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ShowWindow(hwnd, SW_RESTORE);
            // a synthetic Alt press lets SetForegroundWindow succeed from a background process
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(250);
            if (GetForegroundWindow() == hwnd) return true;
        }
        return false;
    }

    static void SelectTab(IntPtr hwnd, int index)
    {
        try
        {
            var tabs = RealTabs(AutomationElement.FromHandle(hwnd));
            if (index >= 0 && index < tabs.Count && tabs[index].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p))
                ((SelectionItemPattern)p).Select();
        }
        catch { }
    }

    static HashSet<IntPtr> CabinetWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) => { if (IsExplorerWindow(h) && IsWindowVisible(h)) set.Add(h); return true; }, IntPtr.Zero);
        return set;
    }

    static string ClassOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
