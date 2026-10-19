using System.IO;
using System.Management;
using System.Text;
using static WinResume.Core.Native;

namespace WinResume.Core;

public static class Capture
{
    /// <summary>Shell/system hosts that show windows but are not apps the user would want back.</summary>
    static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost.exe", "SearchHost.exe", "StartMenuExperienceHost.exe",
        "ShellExperienceHost.exe", "LockApp.exe", "SystemSettingsBroker.exe",
    };

    /// <param name="includeExplorer">
    /// Reading Explorer tabs briefly selects each tab and focuses the address bar. Pass false for
    /// quiet snapshots that must not disturb the user (and for tests that don't involve Explorer).
    /// </param>
    /// <param name="includeTerminal">
    /// Also record Windows Terminal tabs (directory and activated Python environment) in
    /// <see cref="Snapshot.Terminal"/>. Off by default; the terminal code lives in the Terminal folder.
    /// </param>
    public static Snapshot TakeSnapshot(bool includeExplorer = true, bool includeTerminal = false)
    {
        var windows = new List<(IntPtr Hwnd, uint Pid)>();
        EnumWindows((hwnd, _) =>
        {
            if (IsAppWindow(hwnd)) { GetWindowThreadProcessId(hwnd, out var pid); windows.Add((hwnd, pid)); }
            return true;
        }, IntPtr.Zero);

        var cmdLines = LoadCommandLines();
        var apps = new List<AppEntry>();
        var seenPids = new HashSet<uint>();
        var seenAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hwnd, pid) in windows)
        {
            var exe = ExePathOf(pid);
            if (exe is null)
            {
                // Could not open the process: almost certainly elevated.
                apps.Add(new AppEntry { Title = TitleOf(hwnd), Inspectable = false, Rect = Rect(hwnd, out var s0), State = s0 });
                continue;
            }
            var exeName = Path.GetFileName(exe);
            if (Ignored.Contains(exeName)) continue;

            // File Explorer: every window separately, with all of its tabs.
            if (ExplorerTabs.IsExplorerWindow(hwnd))
            {
                if (!includeExplorer) continue;
                var (paths, active) = ExplorerTabs.Capture(hwnd);
                if (paths.Count == 0) continue;
                apps.Add(new AppEntry
                {
                    Kind = AppKind.Explorer, ExePath = exe, Title = TitleOf(hwnd),
                    Rect = Rect(hwnd, out var se), State = se,
                    ExplorerTabs = paths, ExplorerActiveTab = active,
                });
                continue;
            }

            // Store apps hosted in ApplicationFrameHost: the real app is a child window in another process.
            var appPid = pid;
            var appExe = exe;
            if (exeName.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var inner = RealAppPid(hwnd, pid);
                if (inner is null) continue;
                appPid = inner.Value;
                appExe = ExePathOf(appPid) ?? exe;
            }

            // Word / Excel / PowerPoint: reopen the exact documents.
            if (Office.IsOfficeApp(appExe) && Office.ReadDocuments(appExe) is { } docs)
            {
                if (!seenPids.Add(appPid)) continue;
                apps.Add(new AppEntry
                {
                    Kind = AppKind.Office, ExePath = appExe, Title = TitleOf(hwnd),
                    Documents = docs.Paths, UnsavedDocuments = docs.Unsaved,
                    Rect = Rect(hwnd, out var so), State = so,
                });
                continue;
            }

            // Chromium browsers: one entry per profile that has windows open.
            if (Chromium.IsChromium(appExe))
            {
                if (!seenPids.Add(appPid)) continue;
                var profiles = Chromium.OpenProfiles(appExe);
                if (profiles.Count > 0)
                {
                    foreach (var profile in profiles)
                        apps.Add(new AppEntry
                        {
                            Kind = AppKind.Chromium, ExePath = appExe, ChromiumProfile = profile,
                            Title = $"{Path.GetFileNameWithoutExtension(appExe)} ({profile})",
                        });
                    continue;
                }
            }

            var aumid = AumidOf(appPid);
            // With terminal capture on, the terminal module recreates Windows Terminal itself, tab by tab;
            // relaunching the bare app as well would open an extra empty window.
            if (includeTerminal && aumid?.StartsWith("Microsoft.WindowsTerminal", StringComparison.OrdinalIgnoreCase) == true) continue;
            if (aumid is not null)
            {
                if (!seenAumids.Add(aumid)) continue;
                apps.Add(new AppEntry
                {
                    Kind = AppKind.Packaged, ExePath = appExe, Aumid = aumid, Title = TitleOf(hwnd),
                    Rect = Rect(hwnd, out var sp), State = sp,
                });
                continue;
            }

            if (!seenPids.Add(appPid)) continue; // one entry per process: relaunching reopens its windows
            apps.Add(new AppEntry
            {
                ExePath = appExe,
                CommandLine = cmdLines.TryGetValue(appPid, out var cl) ? cl : $"\"{appExe}\"",
                Title = TitleOf(hwnd),
                Rect = Rect(hwnd, out var state),
                State = state,
            });
        }
        return new Snapshot { Apps = apps, Terminal = includeTerminal ? Terminal.TerminalScanner.Scan() : null };
    }

    static bool IsAppWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int));
        if (cloaked != 0) return false; // hidden UWP / other virtual-desktop windows
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var owner = GetWindow(hwnd, GW_OWNER);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (owner != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return false;
        return true;
    }

    static uint? RealAppPid(IntPtr frameHwnd, uint hostPid)
    {
        uint? found = null;
        EnumChildWindows(frameHwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var p);
            if (p != hostPid) { found = p; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static string? AumidOf(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int len = 256;
            var sb = new StringBuilder(len);
            return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static string TitleOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(GetWindowTextLength(hwnd) + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string? ExePathOf(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static WindowRect Rect(IntPtr hwnd, out ShowState state)
    {
        var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref wp);
        state = wp.showCmd switch
        {
            SW_SHOWMAXIMIZED => ShowState.Maximized,
            SW_SHOWMINIMIZED => ShowState.Minimized,
            _ => ShowState.Normal,
        };
        var r = wp.rcNormalPosition;
        return new WindowRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    static Dictionary<uint, string> LoadCommandLines()
    {
        var map = new Dictionary<uint, string>();
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
        foreach (ManagementObject o in searcher.Get())
        {
            if (o["CommandLine"] is string cl && o["ProcessId"] is uint pid) map[pid] = cl;
        }
        return map;
    }
}
