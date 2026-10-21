using System.IO;
using System.Text.Json;

namespace WinResume.Core;

public sealed record Settings
{
    /// <summary>Capture and restore Windows Terminal tabs (folder, environment, scrollback via Windows Terminal itself).</summary>
    public bool RestoreTerminal { get; init; } = true;
    /// <summary>Stop capturing entirely. Nothing is saved at shutdown while this is on.</summary>
    public bool Paused { get; init; }
    /// <summary>
    /// Crash protection: also save the open apps in the background every <see cref="AutoSaveMinutes"/> minutes, so a
    /// power cut (which gives no shutdown message) loses at most that much. Off by default; nothing is recorded
    /// while you work unless this is on.
    /// </summary>
    public bool AutoSave { get; init; }
    public int AutoSaveMinutes { get; init; } = 1;
    /// <summary>Executable names to leave out of both capture and restore, e.g. "Discord.exe".</summary>
    public List<string> Excluded { get; init; } = new();
}

/// <summary>
/// The one saved session and the settings, as plain JSON files in %LocalAppData%\WinResume.
/// Only the latest session is kept, and it is deleted once restored. With crash protection on there is also
/// autosave.json, the latest background save; a session saved at shutdown always wins over it.
/// </summary>
public static class SessionStore
{
    /// <summary>Where the files live. Tests point this at a temp folder.</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinResume");

    public static string SessionFile => Path.Combine(Folder, "last-session.json");
    public static string AutosaveFile => Path.Combine(Folder, "autosave.json");
    public static string SettingsFile => Path.Combine(Folder, "settings.json");
    public static string LogFile => Path.Combine(Folder, "log.txt");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static bool HasSession => File.Exists(SessionFile) || File.Exists(AutosaveFile);

    /// <summary>The cleaned autosave as last written (without its timestamp), to skip writes when nothing changed.</summary>
    static string? lastAutosave;

    /// <summary>
    /// Saves the session at shutdown, without window titles, which can contain page, chat and document names.
    /// This is the complete, final save, so the background autosave is no longer needed.
    /// </summary>
    public static void Save(Snapshot snapshot)
    {
        Write(SessionFile, Clean(snapshot));
        DeleteAutosave();
    }

    /// <summary>Background save. Returns false, writing nothing, when the apps are the same as the last autosave.</summary>
    public static bool SaveAutosave(Snapshot snapshot)
    {
        var clean = Clean(snapshot);
        var content = (clean with { TakenAtUtc = default }).ToJson();
        if (content == lastAutosave && File.Exists(AutosaveFile)) return false;
        Write(AutosaveFile, clean);
        lastAutosave = content;
        return true;
    }

    static Snapshot Clean(Snapshot snapshot) =>
        snapshot with { Apps = snapshot.Apps.Select(a => a with { Title = "" }).ToList() };

    /// <summary>Written to a temporary file and then renamed, so a power cut mid-write cannot leave a broken file.</summary>
    static void Write(string path, Snapshot snapshot)
    {
        Directory.CreateDirectory(Folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, snapshot.ToJson());
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The shutdown session if there is one, otherwise the latest autosave (the machine lost power).</summary>
    public static Snapshot? Load()
    {
        foreach (var file in new[] { SessionFile, AutosaveFile })
        {
            try { if (File.Exists(file)) return Snapshot.FromJson(File.ReadAllText(file)); }
            catch (JsonException) { }
        }
        return null;
    }

    public static void Delete()
    {
        try { File.Delete(SessionFile); } catch (IOException) { }
        DeleteAutosave();
    }

    public static void DeleteAutosave()
    {
        lastAutosave = null;
        try { File.Delete(AutosaveFile); } catch (IOException) { }
    }

    public static Settings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsFile)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile), Options) ?? new Settings()
                : new Settings();
        }
        catch (JsonException) { return new Settings(); }
    }

    public static void SaveSettings(Settings settings)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, Options));
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never break shutdown */ }
    }
}
