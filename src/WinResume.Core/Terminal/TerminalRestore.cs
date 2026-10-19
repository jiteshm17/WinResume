using System.Diagnostics;
using System.IO;

namespace WinResume.Core.Terminal;

/// <summary>
/// Reopens terminal tabs with wt.exe: one new window per saved window, one tab per saved tab, each in its
/// directory with its Python environment activated. Nothing else is run; the tab simply waits at the prompt.
/// </summary>
public static class TerminalRestore
{
    /// <summary>The wt.exe command line for each saved window. Pure, so it can be tested without launching anything.</summary>
    public static List<string> BuildCommandLines(TerminalSnapshot snapshot) =>
        snapshot.Tabs
            .GroupBy(t => t.WindowId)
            .Select(window => "-w new " + string.Join(" ; ", window.OrderBy(t => t.Order).Select(BuildTab)))
            .ToList();

    static string BuildTab(TerminalTab tab)
    {
        var dir = !string.IsNullOrEmpty(tab.Directory) && Directory.Exists(tab.Directory)
            ? $" -d {Quote(tab.Directory)}" : "";
        return $"new-tab{dir} {ShellCommandLine(tab)}";
    }

    /// <summary>The shell with the arguments that keep it open and, if there is one, activate the saved environment.</summary>
    public static string ShellCommandLine(TerminalTab tab)
    {
        var activation = ActivationCommand(tab);
        var shell = Quote(tab.ShellPath);
        var name = Path.GetFileName(tab.ShellPath).ToLowerInvariant();
        if (name == "cmd.exe")
            return activation is null ? $"{shell} /k" : $"{shell} /k {Quote(activation)}";

        // pwsh.exe / powershell.exe: activate the environment, then put the last command in history
        var steps = new List<string>();
        if (activation is not null) steps.Add(activation);
        if (!string.IsNullOrWhiteSpace(tab.LastCommand)) steps.Add(CommandHistory.AddToHistoryScript(tab.LastCommand));
        return steps.Count == 0 ? $"{shell} -NoExit" : $"{shell} -NoExit -Command {Quote(string.Join("; ", steps))}";
    }

    /// <summary>The line that activates the saved environment, or null when there is nothing to activate.</summary>
    public static string? ActivationCommand(TerminalTab tab)
    {
        var isCmd = Path.GetFileName(tab.ShellPath).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
        switch (tab.EnvKind)
        {
            case EnvKind.Venv when !string.IsNullOrEmpty(tab.EnvPath):
                var script = Path.Combine(tab.EnvPath!, "Scripts", isCmd ? "activate.bat" : "Activate.ps1");
                if (!File.Exists(script)) return null; // the environment was deleted or moved
                return isCmd ? $"\"{script}\"" : $"& '{script.Replace("'", "''")}'";
            case EnvKind.Conda when !string.IsNullOrEmpty(tab.EnvPath):
                return isCmd ? $"conda activate {tab.EnvPath}" : $"conda activate '{tab.EnvPath!.Replace("'", "''")}'";
            default:
                return null;
        }
    }

    public static int Restore(TerminalSnapshot snapshot, Action<string>? log = null)
    {
        var launched = 0;
        foreach (var line in BuildCommandLines(snapshot))
        {
            Process.Start(new ProcessStartInfo("wt.exe", line) { UseShellExecute = true });
            log?.Invoke($"wt {line}");
            launched++;
            Thread.Sleep(1500); // let each window open before the next
        }
        return launched;
    }

    static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
