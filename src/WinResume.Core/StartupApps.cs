using System.IO;
using Microsoft.Win32;

namespace WinResume.Core;

/// <summary>
/// The apps Windows itself starts at sign-in and that are switched on (the list in Task Manager's Startup apps tab,
/// plus Store apps' startup tasks). Restoring one of these would open it twice, so the restore skips them.
/// </summary>
public static class StartupApps
{
    /// <param name="Commands">Command lines of enabled startup entries (Run keys and Startup folders).</param>
    /// <param name="PackageFamilies">Package family names of Store apps with an enabled startup task.</param>
    public sealed record Info(IReadOnlyList<string> Commands, IReadOnlySet<string> PackageFamilies)
    {
        public static Info Empty { get; } = new(Array.Empty<string>(), new HashSet<string>());
    }

    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    const string SystemAppData = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    public static Info Read()
    {
        var commands = new List<string>();
        try
        {
            ReadRunKey(Registry.CurrentUser, Run, commands);
            ReadRunKey(Registry.LocalMachine, Run, commands);
            ReadRunKey(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", commands);
            ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), commands);
            ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), commands);
        }
        catch { /* an unreadable source must not stop the restore */ }
        return new Info(commands, ReadPackageFamilies());
    }

    static void ReadRunKey(RegistryKey hive, string path, List<string> into)
    {
        using var run = hive.OpenSubKey(path);
        if (run is null) return;
        foreach (var name in run.GetValueNames())
        {
            if (run.GetValue(name) is not string command || command.Length == 0) continue;
            // Task Manager's toggle is recorded per user, for both machine-wide and per-user entries.
            if (IsDisabled(Registry.CurrentUser, Approved + @"\Run", name) ||
                IsDisabled(Registry.CurrentUser, Approved + @"\Run32", name) ||
                IsDisabled(Registry.LocalMachine, Approved + @"\Run", name)) continue;
            into.Add(command);
        }
    }

    static void ReadStartupFolder(string folder, List<string> into)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder))
        {
            if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsDisabled(Registry.CurrentUser, Approved + @"\StartupFolder", Path.GetFileName(file)) ||
                IsDisabled(Registry.LocalMachine, Approved + @"\StartupFolder", Path.GetFileName(file))) continue;
            into.Add(file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) ?? file : file);
        }
    }

    static bool IsDisabled(RegistryKey hive, string path, string name)
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue(name) is byte[] value && IsDisabledFlag(value);
    }

    /// <summary>Task Manager stores 02/06 for on and 03/07 for off in the first byte of the record.</summary>
    public static bool IsDisabledFlag(byte[]? record) => record is { Length: > 0 } && (record[0] & 1) == 1;

    static string? ShortcutTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(lnk);
            string target = link.TargetPath, args = link.Arguments;
            return string.IsNullOrWhiteSpace(target) ? null : $"\"{target}\" {args}".TrimEnd();
        }
        catch { return null; }
    }

    static HashSet<string> ReadPackageFamilies()
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SystemAppData);
            if (root is null) return families;
            foreach (var family in root.GetSubKeyNames())
            {
                using var package = root.OpenSubKey(family);
                if (package is null) continue;
                foreach (var task in package.GetSubKeyNames())
                {
                    using var t = package.OpenSubKey(task);
                    if (t?.GetValue("State") is int state && IsEnabledTaskState(state)) { families.Add(family); break; }
                }
            }
        }
        catch { }
        return families;
    }

    /// <summary>StartupTaskState: 2 = enabled, 4 = enabled by policy (0 disabled, 1 disabled by user, 3 disabled by policy).</summary>
    public static bool IsEnabledTaskState(int state) => state is 2 or 4;

    /// <summary>
    /// True when one of the enabled startup commands launches this executable: either it runs it directly, or names it as
    /// an argument (updater stubs such as Update.exe --processStart Discord.exe).
    /// </summary>
    public static bool CommandLaunches(string commandLine, string exePath)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(exePath)) return false;
        if (commandLine.Contains(exePath, StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(exePath);
        if (name.Length == 0) return false;
        var at = 0;
        while ((at = commandLine.IndexOf(name, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            // must be a whole file name, not the tail of a longer one (e.g. "NotCode.exe")
            var before = at == 0 ? ' ' : commandLine[at - 1];
            if (before is ' ' or '\\' or '/' or '"' or '=') return true;
            at += name.Length;
        }
        return false;
    }

    public static bool StartsItself(AppEntry app, Info info)
    {
        if (app.Kind == AppKind.Packaged && app.Aumid is { } aumid &&
            info.PackageFamilies.Contains(aumid.Split('!')[0])) return true;
        return info.Commands.Any(c => CommandLaunches(c, app.ExePath));
    }
}
