using System.IO;
using System.Text.Json;

namespace WinResume.Core;

public sealed record Settings
{
    /// <summary>Capture and restore Windows Terminal tabs (folder, environment, scrollback via Windows Terminal itself).</summary>
    public bool RestoreTerminal { get; init; } = true;
    /// <summary>Stop capturing entirely. Nothing is saved at shutdown while this is on.</summary>
    public bool Paused { get; init; }
    /// <summary>Executable names to leave out of both capture and restore, e.g. "Discord.exe".</summary>
    public List<string> Excluded { get; init; } = new();
}

/// <summary>
/// The one saved session and the settings, as plain JSON files in %LocalAppData%\WinResume.
/// Only the latest session is kept, and it is deleted once restored.
/// </summary>
public static class SessionStore
{
    /// <summary>Where the files live. Tests point this at a temp folder.</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinResume");

    public static string SessionFile => Path.Combine(Folder, "last-session.json");
    public static string SettingsFile => Path.Combine(Folder, "settings.json");
    public static string LogFile => Path.Combine(Folder, "log.txt");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static bool HasSession => File.Exists(SessionFile);

    /// <summary>Saves the session without window titles, which can contain page, chat and document names.</summary>
    public static void Save(Snapshot snapshot)
    {
        Directory.CreateDirectory(Folder);
        var clean = snapshot with { Apps = snapshot.Apps.Select(a => a with { Title = "" }).ToList() };
        var temp = SessionFile + ".tmp";
        File.WriteAllText(temp, clean.ToJson());
        File.Move(temp, SessionFile, overwrite: true);
    }

    public static Snapshot? Load()
    {
        try { return File.Exists(SessionFile) ? Snapshot.FromJson(File.ReadAllText(SessionFile)) : null; }
        catch (JsonException) { return null; }
    }

    public static void Delete()
    {
        try { File.Delete(SessionFile); } catch (IOException) { }
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
