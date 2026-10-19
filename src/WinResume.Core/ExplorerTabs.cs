using System.Windows.Automation;
using static WinResume.Core.Native;

namespace WinResume.Core;

/// <summary>
/// File Explorer (Windows 11) lists every tab of a window as its own Shell.Application entry (all sharing the
/// window's HWND), and each entry can be read and navigated. Capture and restore therefore never select tabs,
/// move focus or send keys: capture reads the entries, restore clicks the window's own "Add New Tab" button
/// and navigates the new tab's entry. UI Automation is used only to read the tab order and which tab is active.
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

    /// <summary>
    /// One line for the log: every Explorer window that exists (even hidden or cloaked ones) and how many tabs
    /// Shell reports for it. Written at each shutdown so a missing Explorer entry can be explained afterwards.
    /// </summary>
    public static string Describe()
    {
        try
        {
            var windows = new List<IntPtr>();
            EnumWindows((h, _) => { if (IsExplorerWindow(h)) windows.Add(h); return true; }, IntPtr.Zero);
            if (windows.Count == 0) return "explorer: no Explorer windows exist";
            var parts = windows.Select(h =>
            {
                DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int));
                return $"0x{h.ToInt64():X} visible={IsWindowVisible(h)} cloaked={cloaked != 0} shellTabs={ShellTabs(h).Count} uiaTabs={TabCount(h)}";
            });
            return "explorer: " + string.Join("; ", parts);
        }
        catch (Exception e) { return "explorer: could not inspect windows: " + e.Message; }
    }

    /// <summary>
    /// Returns tab locations in order plus the index of the active tab. Purely passive: no tab is selected
    /// and nothing is focused, so the user's windows do not move or flicker.
    /// </summary>
    public static (List<string> Paths, int Active) Capture(IntPtr hwnd)
    {
        var entries = ShellTabs(hwnd).Select(t => (t.Title, Path: (string?)LocationOf(t.Window))).ToList();
        var names = new List<string>();
        int active = 0;
        try
        {
            var tabs = RealTabs(AutomationElement.FromHandle(hwnd));
            for (int i = 0; i < tabs.Count; i++)
            {
                names.Add(tabs[i].Current.Name);
                if (tabs[i].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && p is SelectionItemPattern sel
                    && sel.Current.IsSelected) active = i;
            }
        }
        catch { /* order falls back to the order Shell lists the tabs in */ }

        var paths = OrderByTabStrip(entries, names).Where(p => p is not null).Select(p => NormalizeLocation(p!)).ToList();
        return (paths, Math.Min(active, Math.Max(0, paths.Count - 1)));
    }

    /// <summary>
    /// Shell lists tabs in creation order; the tab strip can have been rearranged by dragging. Orders the entries
    /// like the strip by matching each tab's name to the entry's folder title. Entries that match nothing keep
    /// their place at the end.
    /// </summary>
    public static List<string?> OrderByTabStrip(IReadOnlyList<(string Title, string? Path)> entries, IReadOnlyList<string> tabNames)
    {
        var left = entries.ToList();
        var ordered = new List<string?>();
        foreach (var name in tabNames)
        {
            var i = left.FindIndex(e => string.Equals(e.Title, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            ordered.Add(left[i].Path);
            left.RemoveAt(i);
        }
        ordered.AddRange(left.Select(e => e.Path));
        return ordered;
    }

    static string? LocationOf(dynamic window)
    {
        try { return (string)window.Document.Folder.Self.Path; }
        catch { return null; }
    }

    /// <summary>One Shell.Application entry per tab of the window, in creation order.</summary>
    static List<(string Title, dynamic Window)> ShellTabs(IntPtr hwnd)
    {
        var result = new List<(string, dynamic)>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return result;
        try
        {
            dynamic shell = Activator.CreateInstance(type)!;
            foreach (var w in shell.Windows())
            {
                try
                {
                    if ((long)w.HWND != hwnd.ToInt64()) continue;
                    string title = w.Document.Folder.Title;
                    result.Add((title, w));
                }
                catch { /* non-Explorer shell window, or a tab still loading */ }
            }
        }
        catch { }
        return result;
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

    const string HomeClsid = "::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}";

    /// <summary>Turns display names ("Downloads") and "::{CLSID}" locations into names Explorer can open (shell:...).</summary>
    public static string NormalizeLocation(string value)
    {
        var v = value.Trim();
        if (KnownFolders.TryGetValue(v, out var shell)) return shell;
        // Folders without a file-system path (Home, This PC, ...) come back from Shell as "::{CLSID}"
        if (v.StartsWith("::{", StringComparison.Ordinal))
            return v.StartsWith(HomeClsid, StringComparison.OrdinalIgnoreCase) ? KnownFolders["Home"] : "shell:" + v;
        return v;
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

        // The new tab shows up as one more Shell entry; it is the newest, so the last one. Navigating the entry
        // sends the tab there without focusing the window or typing into it.
        List<(string Title, dynamic Window)> tabs = new();
        for (int w = 0; w < 30 && (tabs = ShellTabs(hwnd)).Count <= count; w++) Thread.Sleep(150);
        if (tabs.Count <= count) return false;
        try { tabs[^1].Window.Navigate2(path); }
        catch (Exception e) { log?.Invoke($"explorer: navigate failed: {e.Message}"); return false; }
        Thread.Sleep(400);
        return true;
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
