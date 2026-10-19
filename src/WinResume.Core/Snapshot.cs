using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinResume.Core;

public enum ShowState { Normal, Minimized, Maximized }

/// <summary>How an entry is brought back. Generic Win32 is the default for any app.</summary>
public enum AppKind { Win32, Packaged, Chromium, Explorer, Office }

public sealed record WindowRect(int Left, int Top, int Right, int Bottom);

public sealed record AppEntry
{
    public AppKind Kind { get; init; } = AppKind.Win32;
    public string ExePath { get; init; } = "";
    public string CommandLine { get; init; } = "";
    public string Title { get; init; } = "";
    public WindowRect Rect { get; init; } = new(0, 0, 0, 0);
    public ShowState State { get; init; } = ShowState.Normal;
    /// <summary>False when the process could not be inspected, usually because it is elevated.</summary>
    public bool Inspectable { get; init; } = true;

    /// <summary>Packaged (Store/MSIX) apps: launched through shell:AppsFolder\{Aumid}.</summary>
    public string? Aumid { get; init; }

    /// <summary>Chromium browsers: profile directory name, e.g. "Default" or "Profile 21".</summary>
    public string? ChromiumProfile { get; init; }

    /// <summary>Office apps: full paths (or URLs) of the open documents, and how many are unsaved.</summary>
    public List<string>? Documents { get; init; }
    public int UnsavedDocuments { get; init; }

    /// <summary>Explorer windows: one path per tab, in tab order.</summary>
    public List<string>? ExplorerTabs { get; init; }
    public int ExplorerActiveTab { get; init; }
}

public sealed record Snapshot
{
    public DateTime TakenAtUtc { get; init; } = DateTime.UtcNow;
    public List<AppEntry> Apps { get; init; } = new();

    /// <summary>Windows Terminal tabs. Null unless terminal capture was switched on.</summary>
    public Terminal.TerminalSnapshot? Terminal { get; init; }

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static Snapshot FromJson(string json) =>
        JsonSerializer.Deserialize<Snapshot>(json, Options) ?? new Snapshot();
}
