using System.IO;
using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinResume.Core.Terminal;

/// <summary>How a tab's Python environment was activated.</summary>
public enum EnvKind { None, Venv, Conda }

/// <summary>
/// Where a tab's directory came from, best first. PowerShell's own location is not what Windows
/// reports for the process, so the process directory is the last resort.
/// </summary>
public enum DirectorySource { Hook, ChildProcess, VenvParent, Process }

public sealed record TerminalTab
{
    /// <summary>The shell running in the tab, e.g. C:\Program Files\PowerShell\7\pwsh.exe</summary>
    public string ShellPath { get; init; } = "";
    public string Directory { get; init; } = "";
    public DirectorySource DirectorySource { get; init; } = DirectorySource.Process;
    public EnvKind EnvKind { get; init; } = EnvKind.None;
    /// <summary>venv: the folder in VIRTUAL_ENV. conda: the environment name.</summary>
    public string? EnvPath { get; init; }
    /// <summary>
    /// The program the tab was running when captured (e.g. "python .\app.py"), or null at an empty prompt.
    /// Restored into the tab's history so the up arrow recalls it; never run automatically.
    /// </summary>
    public string? LastCommand { get; init; }
    /// <summary>Position among the tabs of its window, by shell start time.</summary>
    public int Order { get; init; }
    /// <summary>Windows Terminal process the tab belongs to (one per window).</summary>
    public int WindowId { get; init; }
}

public sealed record TerminalSnapshot
{
    public List<TerminalTab> Tabs { get; init; } = new();

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static TerminalSnapshot FromJson(string json) =>
        JsonSerializer.Deserialize<TerminalSnapshot>(json, Options) ?? new TerminalSnapshot();
}

/// <summary>Finds Windows Terminal tabs and reads each one's directory and activated Python environment.</summary>
public static class TerminalScanner
{
    static readonly string[] Wanted = { "WT_SESSION", "VIRTUAL_ENV", "CONDA_PREFIX", "CONDA_DEFAULT_ENV" };

    /// <summary>Folder where an optional PowerShell prompt hook writes each tab's real location.</summary>
    public static string HookFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinResume", "terminal-tabs");

    sealed record Proc(int Pid, int ParentPid, string Name, string? Path, DateTime Created, string? CommandLine);

    public static TerminalSnapshot Scan()
    {
        var procs = LoadProcesses();
        var tabs = new List<TerminalTab>();

        var pseudoWindowOwners = PseudoConsoleOwners();

        foreach (var wt in procs.Where(p => p.Name.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase)))
        {
            // Each tab (and split pane) is a shell started directly by Windows Terminal; its console host
            // (OpenConsole.exe) is a sibling, so that is excluded.
            var shells = procs
                .Where(p => p.ParentPid == wt.Pid && !p.Name.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Created)
                .ToList();

            // One Windows Terminal process hosts all of its windows. Each shell has a hidden
            // pseudo-console window that is owned by the terminal window the tab lives in.
            var byWindow = shells.GroupBy(s =>
                pseudoWindowOwners.TryGetValue((uint)s.Pid, out var owner) ? owner.ToInt64() : (long)wt.Pid);

            foreach (var window in byWindow)
            {
                var ordered = window.OrderBy(p => p.Created).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var tab = DescribeShell(ordered[i], procs, (int)(window.Key & 0x7FFFFFFF), i);
                    if (tab is not null) tabs.Add(tab);
                }
            }
        }
        return new TerminalSnapshot { Tabs = tabs };
    }

    /// <summary>Shell process id → the terminal window that owns that shell's pseudo-console window.</summary>
    static Dictionary<uint, IntPtr> PseudoConsoleOwners()
    {
        var map = new Dictionary<uint, IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            var cls = new System.Text.StringBuilder(64);
            Native.GetClassName(h, cls, 64);
            if (cls.ToString() == "PseudoConsoleWindow")
            {
                Native.GetWindowThreadProcessId(h, out var pid);
                var owner = Native.GetWindow(h, Native.GW_OWNER);
                if (owner != IntPtr.Zero) map[pid] = owner;
            }
            return true;
        }, IntPtr.Zero);
        return map;
    }

    static TerminalTab? DescribeShell(Proc shell, List<Proc> all, int windowId, int order)
    {
        if (shell.Path is null) return null;
        var facts = ProcessMemory.Read((uint)shell.Pid, Wanted);
        if (facts is null) return null;

        var (kind, envPath) = PythonEnvironment.FromVariables(facts.Variables);

        string? hook = null;
        if (facts.Variables.TryGetValue("WT_SESSION", out var session))
            hook = ReadHook(session);

        // What the shell launched in this tab is running where the shell really was when it was started.
        string? child = null;
        string? lastCommand = null;
        var firstChild = all
            .Where(p => p.ParentPid == shell.Pid && CommandHistory.Friendly(p.CommandLine, null) is not null)
            .OrderBy(p => p.Created).FirstOrDefault();
        if (firstChild is not null)
        {
            child = ProcessMemory.Read((uint)firstChild.Pid, Array.Empty<string>())?.CurrentDirectory;
            lastCommand = CommandHistory.Friendly(firstChild.CommandLine, kind == EnvKind.Venv ? envPath : null);
        }

        var (directory, source) = DirectoryChooser.Choose(hook, child, kind == EnvKind.Venv ? envPath : null, facts.CurrentDirectory);
        return new TerminalTab
        {
            ShellPath = shell.Path,
            Directory = directory,
            DirectorySource = source,
            LastCommand = lastCommand,
            EnvKind = kind,
            EnvPath = envPath,
            Order = order,
            WindowId = windowId,
        };
    }

    static string? ReadHook(string session)
    {
        try
        {
            var file = Path.Combine(HookFolder, session.Trim('{', '}') + ".txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch { return null; }
    }

    static List<Proc> LoadProcesses()
    {
        var list = new List<Proc>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CreationDate, CommandLine FROM Win32_Process");
        foreach (ManagementObject o in searcher.Get())
        {
            try
            {
                var created = o["CreationDate"] is string s ? ManagementDateTimeConverter.ToDateTime(s) : DateTime.MinValue;
                list.Add(new Proc(
                    (int)(uint)o["ProcessId"], (int)(uint)o["ParentProcessId"],
                    (string)o["Name"], o["ExecutablePath"] as string, created, o["CommandLine"] as string));
            }
            catch { }
        }
        return list;
    }
}

/// <summary>Picks the most trustworthy directory for a tab.</summary>
public static class DirectoryChooser
{
    public static (string Directory, DirectorySource Source) Choose(
        string? hook, string? childProcessDirectory, string? venvPath, string? processDirectory)
    {
        if (Usable(hook)) return (Trim(hook!), DirectorySource.Hook);
        if (Usable(childProcessDirectory)) return (Trim(childProcessDirectory!), DirectorySource.ChildProcess);

        // The process directory of a PowerShell tab is where it started, usually the home folder.
        // A venv usually sits in its project folder, which is a much better guess than that.
        var process = Trim(processDirectory ?? "");
        if (Usable(venvPath) && LooksLikeStartDirectory(process))
        {
            var project = Path.GetDirectoryName(Trim(venvPath!));
            if (Usable(project)) return (project!, DirectorySource.VenvParent);
        }
        return (process, DirectorySource.Process);
    }

    static bool LooksLikeStartDirectory(string dir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return dir.Equals(home, StringComparison.OrdinalIgnoreCase) ||
               dir.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase) ||
               dir.Length <= 3;
    }

    static bool Usable(string? path) => !string.IsNullOrWhiteSpace(path);
    static string Trim(string path) => path.Length > 3 ? path.TrimEnd('\\') : path; // keep "C:\"
}

/// <summary>Turns the environment variables an activated environment leaves behind into a description.</summary>
public static class PythonEnvironment
{
    public static (EnvKind Kind, string? Path) FromVariables(IReadOnlyDictionary<string, string> vars)
    {
        if (vars.TryGetValue("VIRTUAL_ENV", out var venv) && !string.IsNullOrWhiteSpace(venv))
            return (EnvKind.Venv, venv);
        // Conda's base environment is always "active"; only a named non-base environment is worth restoring.
        if (vars.TryGetValue("CONDA_DEFAULT_ENV", out var name) && !string.IsNullOrWhiteSpace(name) &&
            !name.Equals("base", StringComparison.OrdinalIgnoreCase))
            return (EnvKind.Conda, name);
        return (EnvKind.None, null);
    }
}
