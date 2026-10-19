using System.IO;
using System.Text.Json;

namespace WinResume.Core;

/// <summary>
/// Chromium browsers (Chrome, Edge) keep every open profile in one process, so the
/// process command line cannot tell profiles apart. The browser records which profiles
/// had windows open in its own "Local State" file instead.
/// </summary>
public static class Chromium
{
    static readonly Dictionary<string, string> UserDataDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome.exe"] = @"Google\Chrome\User Data",
        ["msedge.exe"] = @"Microsoft\Edge\User Data",
    };

    public static bool IsChromium(string exePath) => UserDataDirs.ContainsKey(Path.GetFileName(exePath));

    public static IReadOnlyList<string> OpenProfiles(string exePath, string? localAppData = null)
    {
        if (!UserDataDirs.TryGetValue(Path.GetFileName(exePath), out var rel)) return Array.Empty<string>();
        var root = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var statePath = Path.Combine(root, rel, "Local State");
        if (!File.Exists(statePath)) return Array.Empty<string>();
        try
        {
            return ParseLastActiveProfiles(File.ReadAllText(statePath));
        }
        catch (IOException) { return Array.Empty<string>(); }
    }

    public static IReadOnlyList<string> ParseLastActiveProfiles(string localStateJson)
    {
        using var doc = JsonDocument.Parse(localStateJson);
        if (!doc.RootElement.TryGetProperty("profile", out var profile) ||
            !profile.TryGetProperty("last_active_profiles", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return list.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList();
    }

    /// <summary>
    /// Closes every browser window the way the user would, so the browser saves its session
    /// and exits normally. Never force-kills: returns false if it is still running after the timeout.
    /// </summary>
    public static bool CloseGracefully(string processName = "chrome", TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            var windows = BrowserWindows(processName);
            if (windows.Count == 0 && !System.Diagnostics.Process.GetProcessesByName(processName).Any()) return true;
            foreach (var h in windows)
            {
                try
                {
                    ((System.Windows.Automation.WindowPattern)System.Windows.Automation.AutomationElement.FromHandle(h)
                        .GetCurrentPattern(System.Windows.Automation.WindowPattern.Pattern)).Close();
                }
                catch { }
                Thread.Sleep(300);
            }
            Thread.Sleep(500);
        }
        return !System.Diagnostics.Process.GetProcessesByName(processName).Any();
    }

    static List<IntPtr> BrowserWindows(string processName)
    {
        var result = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            var cls = new System.Text.StringBuilder(64);
            Native.GetClassName(h, cls, 64);
            if (cls.ToString() == "Chrome_WidgetWin_1" && Native.IsWindowVisible(h))
            {
                Native.GetWindowThreadProcessId(h, out var pid);
                try
                {
                    if (System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        result.Add(h);
                }
                catch { }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static string BuildArguments(string profile) =>
        $"--profile-directory=\"{profile}\" --restore-last-session";
}
