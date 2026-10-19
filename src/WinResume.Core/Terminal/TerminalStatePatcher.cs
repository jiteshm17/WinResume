using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinResume.Core.Terminal;

/// <summary>
/// Windows Terminal restores its own windows after a restart (tabs, titles, scrollback) from state.json, but it
/// saves each tab with its start folder and plain shell, losing the folder you had moved to and any activated
/// environment. This fills in those two fields from what was captured at shutdown, so Windows Terminal's own
/// restore brings back the scrollback AND the right folder and environment. It must run while Windows Terminal is
/// not running, because Windows Terminal rewrites the file when it exits.
/// </summary>
public static class TerminalStatePatcher
{
    public sealed record TabChange(int Window, int Tab, string? OldDirectory, string? NewDirectory, string? OldCommandLine, string? NewCommandLine);
    public sealed record Result(string? Json, List<TabChange> Changes, List<string> Notes);

    public static string? FindStateFile()
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) return null;
        foreach (var dir in Directory.GetDirectories(packages, "Microsoft.WindowsTerminal*"))
        {
            var file = Path.Combine(dir, "LocalState", "state.json");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>Pure: returns the patched JSON and a list of what changed. Touches no files.</summary>
    public static Result Patch(string stateJson, TerminalSnapshot snapshot)
    {
        var changes = new List<TabChange>();
        var notes = new List<string>();
        var root = JsonNode.Parse(stateJson);
        if (root?["persistedWindowLayouts"] is not JsonArray windows || windows.Count == 0)
        {
            notes.Add("state.json has no saved windows (is firstWindowPreference set to persistedWindowLayout?)");
            return new Result(null, changes, notes);
        }

        // Saved windows are matched to captured windows by tab count, in order; if the counts don't line up the
        // window is left alone rather than guessed at.
        var captured = snapshot.Tabs.GroupBy(t => t.WindowId).Select(g => g.OrderBy(t => t.Order).ToList()).ToList();
        var used = new HashSet<int>();

        for (int w = 0; w < windows.Count; w++)
        {
            if (windows[w]?["tabLayout"] is not JsonArray layout) continue;
            var newTabs = layout.Where(e => e?["action"]?.GetValue<string>() == "newTab").ToList();
            var match = captured.FindIndex(c => !used.Contains(captured.IndexOf(c)) && c.Count == newTabs.Count);
            if (match < 0)
            {
                notes.Add($"window {w + 1}: no captured window with {newTabs.Count} tab(s); left unchanged");
                continue;
            }
            used.Add(match);

            for (int t = 0; t < newTabs.Count; t++)
            {
                var entry = newTabs[t]!.AsObject();
                var tab = captured[match][t];
                var oldDir = entry["startingDirectory"]?.GetValue<string>();
                var oldCmd = entry["commandline"]?.GetValue<string>();
                string? newDir = null, newCmd = null;

                if (!string.IsNullOrEmpty(tab.Directory) && Directory.Exists(tab.Directory) &&
                    !string.Equals(oldDir, tab.Directory, StringComparison.OrdinalIgnoreCase))
                {
                    entry["startingDirectory"] = tab.Directory;
                    newDir = tab.Directory;
                }
                if (TerminalRestore.ActivationCommand(tab) is not null || !string.IsNullOrWhiteSpace(tab.LastCommand))
                {
                    var command = TerminalRestore.ShellCommandLine(tab);
                    if (!string.Equals(oldCmd, command, StringComparison.Ordinal))
                    {
                        entry["commandline"] = command;
                        newCmd = command;
                    }
                }
                if (newDir is not null || newCmd is not null) changes.Add(new TabChange(w + 1, t + 1, oldDir, newDir, oldCmd, newCmd));
            }
        }
        return new Result(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), changes, notes);
    }

    /// <summary>Patches the real file after backing it up. Refuses while Windows Terminal is running.</summary>
    public static Result Apply(string stateFile, TerminalSnapshot snapshot)
    {
        if (Process.GetProcessesByName("WindowsTerminal").Length > 0)
            return new Result(null, new(), new() { "Windows Terminal is running and would overwrite the change when it exits; close it first" });

        var result = Patch(File.ReadAllText(stateFile), snapshot);
        if (result.Json is null || result.Changes.Count == 0) return result;

        File.Copy(stateFile, stateFile + ".winresume-backup", overwrite: true);
        var temp = stateFile + ".winresume-tmp";
        File.WriteAllText(temp, result.Json);
        File.Move(temp, stateFile, overwrite: true);
        return result;
    }
}
