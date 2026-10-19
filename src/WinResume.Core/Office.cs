using System.IO;
using System.Runtime.InteropServices;

namespace WinResume.Core;

/// <summary>
/// Word, Excel and PowerPoint expose their open documents through COM, so the exact files
/// can be reopened. Documents that were never saved have no path and are only counted.
/// </summary>
public static class Office
{
    /// <param name="AlertsOff">Value of the app's DisplayAlerts property that turns prompts off (the type differs per app).</param>
    sealed record Spec(string ProgId, string Collection, object AlertsOff);

    static readonly Dictionary<string, Spec> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WINWORD.EXE"] = new("Word.Application", "Documents", 0),          // wdAlertsNone
        ["EXCEL.EXE"] = new("Excel.Application", "Workbooks", false),
        ["POWERPNT.EXE"] = new("PowerPoint.Application", "Presentations", 1), // ppAlertsNone
    };

    public static bool IsOfficeApp(string exePath) => Apps.ContainsKey(Path.GetFileName(exePath));

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    static dynamic? Connect(string progId)
    {
        if (CLSIDFromProgID(progId, out var clsid) != 0) return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var obj) == 0 ? obj : null;
    }

    /// <summary>Open documents of a running Office app, or null if it can't be reached.</summary>
    public static (List<string> Paths, int Unsaved)? ReadDocuments(string exePath) =>
        ReadDocumentsDetailed(exePath) is { } r ? (r.Paths, r.UnsavedNames.Count) : null;

    /// <summary>Same as <see cref="ReadDocuments"/> but names the documents that have unsaved changes.</summary>
    public static (List<string> Paths, List<string> UnsavedNames)? ReadDocumentsDetailed(string exePath)
    {
        if (!Apps.TryGetValue(Path.GetFileName(exePath), out var spec)) return null;
        try
        {
            dynamic? app = Connect(spec.ProgId);
            if (app is null) return null;
            var paths = new List<string>();
            var unsaved = new List<string>();
            dynamic items = spec.Collection switch
            {
                "Documents" => app.Documents,
                "Workbooks" => app.Workbooks,
                _ => app.Presentations,
            };
            foreach (dynamic d in items)
            {
                try
                {
                    if (spec.Collection == "Workbooks" && d.Windows.Count == 0) continue; // hidden, e.g. PERSONAL.XLSB
                    string full = d.FullName;
                    if (IsRealLocation(full)) paths.Add(full);
                    if (!IsRealLocation(full) || !(bool)d.Saved) unsaved.Add(full);
                }
                catch { /* one odd document must not stop the rest */ }
            }
            return (paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), unsaved);
        }
        catch { return null; }
    }

    /// <summary>Documents with a real path or URL; "Document1" and "Book1" are unsaved new files.</summary>
    public static bool IsRealLocation(string fullName) =>
        !string.IsNullOrWhiteSpace(fullName) &&
        (Path.IsPathRooted(fullName) || fullName.StartsWith("http", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Quits the app only when every open document is saved, then waits for the process to exit.
    /// Returns false if it refused or the app is still running (for example a dialog is blocking it).
    /// </summary>
    public static bool QuitIfAllSaved(string exePath, TimeSpan? timeout = null)
    {
        var docs = ReadDocuments(exePath);
        if (docs is null || docs.Value.Unsaved > 0) return false;
        if (!SendQuit(Apps[Path.GetFileName(exePath)])) return false;

        var name = Path.GetFileNameWithoutExtension(exePath);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            // An Office app will not exit while any COM client still holds a reference to it,
            // including this process, so drop ours before checking.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (!System.Diagnostics.Process.GetProcessesByName(name).Any()) return true;
            Thread.Sleep(300);
        }
        return false;
    }

    /// <summary>Kept separate so its COM references are out of scope (and collectable) when it returns.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool SendQuit(Spec spec)
    {
        try
        {
            dynamic? app = Connect(spec.ProgId);
            if (app is null) return false;
            // Every document was verified saved just before this call, so suppressing the
            // "keep clipboard / save changes?" prompts cannot discard work and stops them blocking the quit.
            try { app.DisplayAlerts = spec.AlertsOff; } catch { }
            app.Quit();
            return true;
        }
        catch { return false; }
    }

    /// <summary>The documents of <paramref name="wanted"/> that the running app does not already have open.</summary>
    public static List<string> NotYetOpen(string exePath, IEnumerable<string> wanted)
    {
        var open = ReadDocuments(exePath)?.Paths ?? new List<string>();
        return wanted.Where(w => !open.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public static string BuildArguments(IEnumerable<string> paths) =>
        string.Join(" ", paths.Select(p => $"\"{p}\""));
}
