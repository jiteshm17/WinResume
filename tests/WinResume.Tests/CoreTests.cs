using WinResume.Core;

namespace WinResume.Tests;

public class LaunchCommandTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --profile-directory=\"Profile 2\"", "--profile-directory=\"Profile 2\"")]
    [InlineData("C:\\Windows\\notepad.exe C:\\notes.txt", "C:\\notes.txt")]
    [InlineData("\"C:\\a b\\app.exe\"", "")]
    [InlineData("C:\\app.exe", "")]
    [InlineData("", "")]
    public void ArgumentsOf_strips_the_executable_token(string commandLine, string expected) =>
        Assert.Equal(expected, LaunchCommand.ArgumentsOf(commandLine));
}

public class ChromiumTests
{
    [Fact]
    public void Reads_last_active_profiles() =>
        Assert.Equal(new[] { "Default", "Profile 21" },
            Chromium.ParseLastActiveProfiles("{\"profile\":{\"last_active_profiles\":[\"Default\",\"Profile 21\"]}}"));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"profile\":{}}")]
    [InlineData("{\"profile\":{\"last_active_profiles\":\"oops\"}}")]
    public void Missing_or_malformed_profiles_give_empty_list(string json) =>
        Assert.Empty(Chromium.ParseLastActiveProfiles(json));

    [Fact]
    public void Arguments_select_profile_and_restore_session() =>
        Assert.Equal("--profile-directory=\"Profile 21\" --restore-last-session", Chromium.BuildArguments("Profile 21"));

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", true)]
    [InlineData(@"C:\Windows\notepad.exe", false)]
    public void Recognises_chromium_browsers(string exe, bool expected) =>
        Assert.Equal(expected, Chromium.IsChromium(exe));
}

public class ExplorerTabsTests
{
    [Theory]
    [InlineData("Downloads", "shell:Downloads")]
    [InlineData("desktop", "shell:Desktop")]
    [InlineData("This PC", "shell:MyComputerFolder")]
    [InlineData(@"E:\VIds", @"E:\VIds")]
    [InlineData(@"\\nas\share\x", @"\\nas\share\x")]
    public void Known_folder_names_become_shell_paths(string input, string expected) =>
        Assert.Equal(expected, ExplorerTabs.NormalizeLocation(input));

    [Theory]
    [InlineData("::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public void Clsid_locations_become_shell_paths(string input, string expected) =>
        Assert.Equal(expected, ExplorerTabs.NormalizeLocation(input));

    [Fact]
    public void Tabs_follow_the_tab_strip_not_creation_order()
    {
        var entries = new List<(string Title, string? Path)> { ("A", @"C:\a"), ("B", @"C:\b"), ("C", @"C:\c") };
        Assert.Equal(new[] { @"C:\c", @"C:\a", @"C:\b" }, ExplorerTabs.OrderByTabStrip(entries, new[] { "C", "A", "B" }));
    }

    [Fact]
    public void Same_named_tabs_keep_their_relative_order()
    {
        var entries = new List<(string Title, string? Path)> { ("Videos", @"C:\x\Videos"), ("Videos", @"D:\Videos") };
        Assert.Equal(new[] { @"C:\x\Videos", @"D:\Videos" }, ExplorerTabs.OrderByTabStrip(entries, new[] { "Videos", "Videos" }));
    }

    [Fact]
    public void Unmatched_entries_are_kept_after_the_matched_ones()
    {
        var entries = new List<(string Title, string? Path)> { ("A", @"C:\a"), ("B", @"C:\b") };
        Assert.Equal(new[] { @"C:\b", @"C:\a" }, ExplorerTabs.OrderByTabStrip(entries, new[] { "B", "Other" }));
        Assert.Equal(new[] { @"C:\a", @"C:\b" }, ExplorerTabs.OrderByTabStrip(entries, Array.Empty<string>()));
    }
}

public class SnapshotTests
{
    [Fact]
    public void Json_round_trips()
    {
        var snap = new Snapshot
        {
            Apps =
            {
                new AppEntry
                {
                    ExePath = @"C:\Windows\notepad.exe",
                    CommandLine = @"C:\Windows\notepad.exe C:\a.txt",
                    Title = "a.txt - Notepad",
                    Rect = new WindowRect(10, 20, 810, 620),
                    State = ShowState.Maximized,
                },
            },
        };
        var back = Snapshot.FromJson(snap.ToJson());
        Assert.Single(back.Apps);
        Assert.Equal(snap.Apps[0], back.Apps[0]);
    }
}

public class RestoreSummaryTests
{
    static RestoreResult Result(bool launched, string? note = null, bool partial = false, string exe = @"C:\x\app.exe") =>
        new(new AppEntry { ExePath = exe }, launched, false, note) { Partial = partial };

    [Fact]
    public void Counts_restored_skipped_and_problems_separately()
    {
        var summary = RestoreSummary.From(new[]
        {
            Result(true), Result(true, "Chromium restores its own windows"),
            Result(false, "Starts itself at sign-in"), Result(false, "Already running"),
            Result(false, "Not inspectable (probably elevated)", exe: ""),
            Result(true, "Not every tab could be restored", partial: true, exe: @"C:\Windows\explorer.exe"),
        });
        Assert.Equal((2, 2, 2), (summary.Restored, summary.Skipped, summary.NeedAttention));
        Assert.Contains("2 need attention", summary.LogLine);
        Assert.Contains("An app: Not inspectable (probably elevated)", summary.Problems);
        Assert.Contains("explorer: Not every tab could be restored", summary.Problems);
    }

    [Fact]
    public void Message_for_a_clean_restore_has_no_problem_section()
    {
        var message = RestoreSummary.From(new[] { Result(true), Result(false, "Already running") }).Message;
        Assert.Equal("Restored 1 app. 1 was already running or start by themselves.", message);
    }

    [Fact]
    public void Message_for_a_partial_restore_names_the_apps()
    {
        var message = RestoreSummary.From(new[]
        {
            Result(true),
            Result(true, "Not every tab could be restored", partial: true, exe: @"C:\Windows\explorer.exe"),
        }).Message;
        Assert.Contains("1 could not be fully restored", message);
        Assert.Contains("explorer: Not every tab could be restored", message);
    }
}
