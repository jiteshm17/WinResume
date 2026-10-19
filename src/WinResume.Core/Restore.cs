using System.IO;
using System.Diagnostics;
using static WinResume.Core.Native;

namespace WinResume.Core;

public sealed record RestoreResult(AppEntry App, bool Launched, bool Placed, string? Note)
{
    /// <summary>The app was launched but only part of it came back (for example not every Explorer tab).</summary>
    public bool Partial { get; init; }
}

/// <summary>What a restore did, in numbers, for the log and for the notification shown to the user.</summary>
public sealed record RestoreSummary(int Restored, int Skipped, int NeedAttention)
{
    public static RestoreSummary From(IEnumerable<RestoreResult> results)
    {
        int restored = 0, skipped = 0, attention = 0;
        foreach (var r in results)
        {
            if (r.Partial || (!r.Launched && !Restore.IsSkippedOnPurpose(r.Note))) attention++;
            else if (r.Launched) restored++;
            else skipped++;
        }
        return new RestoreSummary(restored, skipped, attention);
    }

    public string LogLine => $"restore complete: {Restored} restored, {Skipped} skipped (already running or start themselves), {NeedAttention} need attention";

    public string Message
    {
        get
        {
            var text = Restored == 1 ? "Restored 1 app." : $"Restored {Restored} apps.";
            if (Skipped > 0) text += $" {Skipped} already running or starting by themselves.";
            if (NeedAttention > 0) text += $" {NeedAttention} could not be fully restored; see the log in the data folder.";
            return text;
        }
    }
}

public static class Restore
{
    public static List<RestoreResult> RestoreAll(Snapshot snapshot, TimeSpan? stagger = null, Action<string>? log = null,
        bool restoreTerminal = false)
    {
        var results = new List<RestoreResult>();
        if (restoreTerminal && snapshot.Terminal is { Tabs.Count: > 0 } terminal)
            Terminal.TerminalRestore.Restore(terminal, log);
        var running = RunningExecutables();
        var startup = StartupApps.Read();
        foreach (var app in snapshot.Apps)
        {
            if (!app.Inspectable)
            {
                results.Add(new RestoreResult(app, false, false, "Not inspectable (probably elevated)"));
                continue;
            }
            if (ShouldSkip(app, running, startup) is { } reason)
            {
                results.Add(new RestoreResult(app, false, false, reason));
                continue;
            }
            results.Add(RestoreOne(app, log));
            if (stagger is { } d) Thread.Sleep(d);
        }
        return results;
    }

    /// <summary>
    /// After a restart many apps start themselves. Relaunching a plain or Store app that is already running
    /// would open a second copy or just hand off, so it is skipped. Browsers, Explorer and Office open
    /// content into a running instance, so they are never skipped here.
    /// </summary>
    /// <returns>The reason to skip it, or null to restore it.</returns>
    public static string? ShouldSkip(AppEntry app, IReadOnlySet<string> runningExecutables, StartupApps.Info? startup = null)
    {
        if (app.Kind is not (AppKind.Win32 or AppKind.Packaged) || app.ExePath.Length == 0) return null;
        // Windows starts these itself at sign-in, possibly a little later than now, so leave them alone.
        if (startup is not null && StartupApps.StartsItself(app, startup)) return StartsItself;
        if (runningExecutables.Contains(app.ExePath)) return AlreadyRunning;
        return null;
    }

    const string StartsItself = "Starts itself at sign-in", AlreadyRunning = "Already running";

    /// <summary>True for the skip reasons that mean nothing is missing, as opposed to a launch that failed.</summary>
    public static bool IsSkippedOnPurpose(string? note) => note is StartsItself or AlreadyRunning;

    static HashSet<string> RunningExecutables()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.MainModule?.FileName is { } path) set.Add(path); }
            catch { /* system or elevated process */ }
            finally { p.Dispose(); }
        }
        return set;
    }

    static RestoreResult RestoreOne(AppEntry app, Action<string>? log)
    {
        try
        {
            switch (app.Kind)
            {
                case AppKind.Explorer:
                    var ok = ExplorerTabs.Restore(app, log);
                    return new RestoreResult(app, true, false, ok ? null : "Not every tab could be restored") { Partial = !ok };

                case AppKind.Office:
                    // If the app is already running with some of the documents open, open only the rest;
                    // launching a second copy would reopen files read-only.
                    var toOpen = Office.NotYetOpen(app.ExePath, app.Documents ?? new());
                    if (app.Documents?.Count > 0 && toOpen.Count == 0)
                        return new RestoreResult(app, true, false, "Already open");
                    Process.Start(new ProcessStartInfo(app.ExePath, Office.BuildArguments(toOpen)) { UseShellExecute = true });
                    log?.Invoke($"launched {Path.GetFileName(app.ExePath)} with {toOpen.Count} document(s)");
                    return new RestoreResult(app, true, false,
                        app.UnsavedDocuments > 0 ? $"{app.UnsavedDocuments} unsaved document(s) cannot be reopened" : null);

                case AppKind.Chromium:
                    Process.Start(new ProcessStartInfo(app.ExePath, Chromium.BuildArguments(app.ChromiumProfile!)) { UseShellExecute = true });
                    log?.Invoke($"launched {Path.GetFileName(app.ExePath)} profile '{app.ChromiumProfile}'");
                    return new RestoreResult(app, true, false, "Chromium restores its own windows");

                case AppKind.Packaged:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.Aumid}") { UseShellExecute = true });
                    log?.Invoke($"launched {app.Aumid}");
                    return new RestoreResult(app, true, false, "Packaged app: window placement not applied");

                default:
                    return LaunchWin32(app, log);
            }
        }
        catch (Exception e)
        {
            return new RestoreResult(app, false, false, e.Message);
        }
    }

    static RestoreResult LaunchWin32(AppEntry app, Action<string>? log)
    {
        var p = Process.Start(new ProcessStartInfo(app.ExePath, LaunchCommand.ArgumentsOf(app.CommandLine))
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(app.ExePath) ?? "",
        });
        if (p is null) return new RestoreResult(app, false, false, "No process returned (single-instance app handed off?)");

        log?.Invoke($"launched {Path.GetFileName(app.ExePath)} (pid {p.Id})");
        var placed = PlaceWhenWindowAppears(p, app);
        return new RestoreResult(app, true, placed, placed ? null : "Window not found in time");
    }

    static bool PlaceWhenWindowAppears(Process p, AppEntry app, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            p.Refresh();
            var hwnd = p.HasExited ? IntPtr.Zero : p.MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                var wp = new WINDOWPLACEMENT
                {
                    length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>(),
                    showCmd = app.State switch
                    {
                        ShowState.Maximized => SW_SHOWMAXIMIZED,
                        ShowState.Minimized => SW_SHOWMINIMIZED,
                        _ => SW_SHOWNORMAL,
                    },
                    rcNormalPosition = new RECT
                    {
                        Left = app.Rect.Left, Top = app.Rect.Top, Right = app.Rect.Right, Bottom = app.Rect.Bottom,
                    },
                };
                return SetWindowPlacement(hwnd, ref wp);
            }
            Thread.Sleep(200);
        }
        return false;
    }
}
