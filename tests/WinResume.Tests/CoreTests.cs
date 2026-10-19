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
