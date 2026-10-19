using System.Diagnostics;
using System.IO;
using WinResume.Core.Terminal;

namespace WinResume.Core;

/// <summary>The two moments the app cares about: the session ending, and the next sign-in.</summary>
public static class SessionFlow
{
    /// <summary>
    /// Called while Windows is shutting down, restarting or signing out. Saves the session, in two steps so a
    /// useful snapshot exists even if the slower Explorer tab reading is cut short.
    /// </summary>
    public static void CaptureForShutdown(Settings settings, TimeSpan budget, Action<string> log)
    {
        // Reading processes (WMI) and other apps' windows (UI Automation) does not work on a UI thread, so the
        // whole capture runs on a background thread while the caller waits at most `budget`.
        var watch = Stopwatch.StartNew();
        var worker = new Thread(() =>
        {
            try
            {
                var fast = Capture.TakeSnapshot(includeExplorer: false, includeTerminal: settings.RestoreTerminal);
                SessionStore.Save(Without(fast, settings));
                log($"saved fast snapshot: {fast.Apps.Count} apps, {fast.Terminal?.Tabs.Count ?? 0} terminal tabs, {watch.ElapsedMilliseconds} ms");

                // Explorer tabs are read by selecting each tab, which takes a few seconds, so they come second.
                var full = Capture.TakeSnapshot(includeExplorer: true, includeTerminal: settings.RestoreTerminal);
                SessionStore.Save(Without(full, settings));
                log($"saved full snapshot incl. Explorer: {full.Apps.Count} apps, {watch.ElapsedMilliseconds} ms total");
            }
            catch (Exception e) { log("capture failed: " + e); }
        });
        worker.SetApartmentState(ApartmentState.MTA);
        worker.IsBackground = true;
        worker.Start();
        if (!worker.Join(budget)) log($"capture still running after {budget.TotalSeconds:0.#} s; continuing with what is saved");
    }

    static Snapshot Without(Snapshot snapshot, Settings settings) =>
        snapshot with { Apps = snapshot.Apps.Where(a => !IsExcluded(a, settings)).ToList() };

    static bool IsExcluded(AppEntry app, Settings settings) =>
        settings.Excluded.Contains(Path.GetFileName(app.ExePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>One line describing what a saved session contains, for the sign-in prompt.</summary>
    public static string Describe(Snapshot snapshot)
    {
        var apps = snapshot.Apps.Count(a => a.Inspectable);
        var tabs = snapshot.Terminal?.Tabs.Count ?? 0;
        return tabs > 0 ? $"{apps} apps and {tabs} terminal tabs" : $"{apps} apps";
    }

    /// <summary>Brings the saved session back, then deletes it.</summary>
    public static void RestoreSession(Snapshot snapshot, Settings settings, Action<string> log)
    {
        // Windows Terminal restores its own windows and scrollback; before it starts, put the folders and
        // environments it does not know about into its saved layout, then start it.
        if (settings.RestoreTerminal && snapshot.Terminal is { Tabs.Count: > 0 } terminal)
        {
            var state = TerminalStatePatcher.FindStateFile();
            if (state is null) log("terminal: Windows Terminal state file not found");
            else
            {
                var patch = TerminalStatePatcher.Apply(state, terminal);
                foreach (var note in patch.Notes) log("terminal: " + note);
                log($"terminal: patched {patch.Changes.Count} tab(s)");
                if (Process.GetProcessesByName("WindowsTerminal").Length == 0)
                {
                    Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true });
                    log("terminal: launched Windows Terminal");
                }
            }
        }

        var apps = snapshot with { Apps = snapshot.Apps.Where(a => !IsExcluded(a, settings)).ToList() };
        foreach (var result in Restore.RestoreAll(apps, TimeSpan.FromMilliseconds(500), log))
            log($"{(result.Launched ? "restored" : "skipped ")} {result.App.Kind,-9} {Path.GetFileName(result.App.ExePath),-22} {result.Note}");

        SessionStore.Delete();
        log("session restored and deleted");
    }
}
