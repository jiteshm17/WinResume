using WinResume.Core;

// Usage:
//   WinResume.Spike snapshot <file.json>                        dump running apps
//   WinResume.Spike restore  <file.json> [--only a.exe,b.exe]   relaunch them (default: everything)
//   WinResume.Spike restore  <file.json> [--skip a.exe,b.exe]   relaunch all except these
if (args.Length < 2) { Console.WriteLine("usage: snapshot|restore <file.json> [--only|--skip exe,exe]"); return 1; }

HashSet<string> ListArg(string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length
        ? new HashSet<string>(args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

switch (args[0])
{
    case "snapshot":
        var snap = Capture.TakeSnapshot(includeExplorer: !args.Contains("--skip-explorer"), includeTerminal: args.Contains("--terminal"));
        File.WriteAllText(args[1], snap.ToJson());
        Console.WriteLine($"{snap.Apps.Count} apps -> {args[1]}");
        foreach (var a in snap.Apps)
            Console.WriteLine($"  {(a.Inspectable ? " " : "!")} {a.Kind,-9} {Path.GetFileName(a.ExePath),-24} {a.Title}");
        return 0;

    case "restore":
        var s = Snapshot.FromJson(File.ReadAllText(args[1]));
        var only = ListArg("--only");
        var skip = ListArg("--skip");
        var filtered = s with
        {
            Apps = s.Apps.Where(a =>
                {
                    var exe = Path.GetFileName(a.ExePath);
                    return (only.Count == 0 || only.Contains(exe)) && !skip.Contains(exe);
                }).ToList(),
        };
        foreach (var r in Restore.RestoreAll(filtered, TimeSpan.FromMilliseconds(500), Console.WriteLine, restoreTerminal: args.Contains("--terminal")))
            Console.WriteLine($"  {(r.Launched ? "OK  " : "FAIL")} {r.App.Kind,-9} placed={r.Placed} {Path.GetFileName(r.App.ExePath),-24} {r.Note}");
        return 0;

    case "terminal-scan":
        var term = WinResume.Core.Terminal.TerminalScanner.Scan();
        File.WriteAllText(args[1], term.ToJson());
        Console.WriteLine($"{term.Tabs.Count} terminal tab(s) -> {args[1]}");
        foreach (var t in term.Tabs)
            Console.WriteLine($"  win={t.WindowId} #{t.Order} {Path.GetFileName(t.ShellPath),-10} {t.Directory,-45} [{t.DirectorySource}] {t.EnvKind} {t.EnvPath}");
        return 0;

    case "terminal-patch":
        // terminal-patch <terminal.json> [--apply] [--state <state.json>]
        // Default is a dry run: shows what would change in Windows Terminal's saved layout and writes nothing.
        var term2 = WinResume.Core.Terminal.TerminalSnapshot.FromJson(File.ReadAllText(args[1]));
        var stateArg = Array.IndexOf(args, "--state");
        var stateFile = stateArg >= 0 ? args[stateArg + 1] : WinResume.Core.Terminal.TerminalStatePatcher.FindStateFile();
        if (stateFile is null) { Console.WriteLine("Windows Terminal state.json not found"); return 1; }
        var patched = args.Contains("--apply")
            ? WinResume.Core.Terminal.TerminalStatePatcher.Apply(stateFile, term2)
            : WinResume.Core.Terminal.TerminalStatePatcher.Patch(File.ReadAllText(stateFile), term2);
        foreach (var n in patched.Notes) Console.WriteLine("note: " + n);
        foreach (var c in patched.Changes)
        {
            Console.WriteLine($"window {c.Window} tab {c.Tab}:");
            if (c.NewDirectory is not null) Console.WriteLine($"   folder  {c.OldDirectory}  ->  {c.NewDirectory}");
            if (c.NewCommandLine is not null) Console.WriteLine($"   command {c.OldCommandLine}  ->  {c.NewCommandLine}");
        }
        Console.WriteLine($"{patched.Changes.Count} tab(s) {(args.Contains("--apply") ? "patched" : "would change (dry run, nothing written)")}");
        return 0;

    case "terminal-restore":
        var saved = WinResume.Core.Terminal.TerminalSnapshot.FromJson(File.ReadAllText(args[1]));
        if (args.Contains("--dry-run"))
            foreach (var line in WinResume.Core.Terminal.TerminalRestore.BuildCommandLines(saved)) Console.WriteLine("wt " + line);
        else
            Console.WriteLine($"{WinResume.Core.Terminal.TerminalRestore.Restore(saved, Console.WriteLine)} window(s) opened");
        return 0;

    // Test helpers used by scripts/kill-and-restore.ps1
    case "close-chrome":
        var closed = Chromium.CloseGracefully(args[1]);
        Console.WriteLine(closed ? $"{args[1]} exited cleanly" : $"{args[1]} still running after timeout (not force-killed)");
        return closed ? 0 : 2;

    case "quit-office":
        var path = args[1];
        if (Office.ReadDocumentsDetailed(path) is { } od && od.UnsavedNames.Count > 0)
            foreach (var n in od.UnsavedNames) Console.WriteLine($"  unsaved: {n}");
        var quit = Office.QuitIfAllSaved(path);
        Console.WriteLine(quit ? $"{Path.GetFileName(path)} quit (all documents were saved)" : $"{Path.GetFileName(path)} NOT closed (unsaved documents or unreachable)");
        return quit ? 0 : 2;

    default:
        Console.WriteLine("unknown command");
        return 1;
}
