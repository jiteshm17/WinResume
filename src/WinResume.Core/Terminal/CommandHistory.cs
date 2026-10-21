using System.IO;

namespace WinResume.Core.Terminal;

/// <summary>
/// Turns the program a tab was running into the command the user most likely typed, so it can be put in the
/// restored tab's history. It is only recalled with the up arrow; it is never run.
/// </summary>
public static class CommandHistory
{
    /// <summary>e.g. "C:\p\.venv\Scripts\python.exe" .\app.py  with venv C:\p\.venv  becomes  python .\app.py</summary>
    public static string? Friendly(string? commandLine, string? venvPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var exe = LaunchCommand.ExecutableOf(commandLine).Replace('/', '\\');
        var args = LaunchCommand.ArgumentsOf(commandLine);

        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Length == 0) return null;
        // the console host that sits under every shell is not something the user ran
        if (name.Equals("conhost", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase)) return null;

        // Inside an activated venv the user typed "python", not the full path to it.
        var inVenv = !string.IsNullOrEmpty(venvPath) &&
                     exe.StartsWith(venvPath.TrimEnd('\\', '/').Replace('/', '\\') + "\\", StringComparison.OrdinalIgnoreCase);
        var program = inVenv ? name : (Path.IsPathRooted(exe) ? name : exe);
        return args.Length == 0 ? program : $"{program} {args}";
    }

    /// <summary>
    /// PowerShell code that appends a line to the PSReadLine history file, which PSReadLine loads when the prompt
    /// starts, so the up arrow recalls it. (PSReadLine's AddToHistory cannot be used here: it fails until the first
    /// prompt.) The line is not run, and it is skipped if it is already the newest entry. Tabs restored together
    /// share this file with PSReadLine, so the write is a single open-append-close call to keep the file locked
    /// for as short a time as possible (a longer hold made PSReadLine print "file is being used by another process").
    /// </summary>
    public static string AddToHistoryScript(string command)
    {
        var quoted = "'" + command.Replace("'", "''") + "'";
        return "try { $h = (Get-PSReadLineOption).HistorySavePath; " +
               $"if ((Get-Content -LiteralPath $h -Tail 1 -ErrorAction SilentlyContinue) -ne {quoted}) {{ [System.IO.File]::AppendAllText($h, {quoted} + [Environment]::NewLine) }} }} catch {{ }}";
    }
}
