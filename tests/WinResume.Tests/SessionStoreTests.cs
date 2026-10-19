using WinResume.Core;
using WinResume.Core.Terminal;

namespace WinResume.Tests;

/// <summary>SessionStore has a static folder, so these must not run in parallel with each other.</summary>
[Collection("SessionStore")]
public class SessionStoreTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "winresume-store-" + Guid.NewGuid().ToString("N"));
    readonly string original = SessionStore.Folder;

    public SessionStoreTests() => SessionStore.Folder = folder;

    public void Dispose()
    {
        SessionStore.Folder = original;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    static Snapshot Sample() => new()
    {
        Apps =
        {
            new AppEntry { ExePath = @"C:\a\app.exe", CommandLine = @"C:\a\app.exe --x", Title = "Secret chat with Alex" },
        },
        Terminal = new TerminalSnapshot { Tabs = { new TerminalTab { ShellPath = "pwsh.exe", Directory = @"C:\p" } } },
    };

    [Fact]
    public void Saved_session_has_no_window_titles()
    {
        SessionStore.Save(Sample());
        Assert.DoesNotContain("Secret chat", File.ReadAllText(SessionStore.SessionFile));
        Assert.Equal("", SessionStore.Load()!.Apps[0].Title);
    }

    [Fact]
    public void Session_round_trips_including_terminal_tabs()
    {
        SessionStore.Save(Sample());
        var back = SessionStore.Load()!;
        Assert.Equal(@"C:\a\app.exe --x", back.Apps[0].CommandLine);
        Assert.Single(back.Terminal!.Tabs);
    }

    [Fact]
    public void Delete_removes_the_only_copy_of_the_session()
    {
        SessionStore.Save(Sample());
        Assert.True(SessionStore.HasSession);
        SessionStore.Delete();
        Assert.False(SessionStore.HasSession);
        Assert.Null(SessionStore.Load());
    }

    [Fact]
    public void A_newer_session_replaces_the_older_one()
    {
        SessionStore.Save(Sample());
        SessionStore.Save(new Snapshot());
        Assert.Empty(SessionStore.Load()!.Apps);
    }

    [Fact]
    public void A_corrupt_session_file_is_treated_as_no_session()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(SessionStore.SessionFile, "{ not json");
        Assert.Null(SessionStore.Load());
    }

    [Fact]
    public void Settings_default_to_terminal_on_and_not_paused()
    {
        var s = SessionStore.LoadSettings();
        Assert.True(s.RestoreTerminal);
        Assert.False(s.Paused);
        Assert.Empty(s.Excluded);
    }

    [Fact]
    public void Settings_round_trip()
    {
        SessionStore.SaveSettings(new Settings { Paused = true, Excluded = { "Discord.exe" } });
        var s = SessionStore.LoadSettings();
        Assert.True(s.Paused);
        Assert.Equal("Discord.exe", Assert.Single(s.Excluded));
    }

    [Fact]
    public void Describe_mentions_terminal_tabs_only_when_there_are_some()
    {
        Assert.Equal("1 apps and 1 terminal tabs", SessionFlow.Describe(Sample()));
        Assert.Equal("1 apps", SessionFlow.Describe(Sample() with { Terminal = null }));
    }
}

public class RestoreSkipTests
{
    static readonly HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase) { @"C:\a\app.exe" };

    [Fact]
    public void A_plain_app_that_is_already_running_is_skipped() =>
        Assert.Equal("Already running", Restore.ShouldSkip(new AppEntry { ExePath = @"C:\a\app.exe" }, Running));

    [Fact]
    public void A_plain_app_that_is_not_running_is_restored() =>
        Assert.Null(Restore.ShouldSkip(new AppEntry { ExePath = @"C:\b\other.exe" }, Running));

    [Theory]
    [InlineData(AppKind.Explorer)]
    [InlineData(AppKind.Chromium)]
    [InlineData(AppKind.Office)]
    public void Apps_that_open_content_into_a_running_instance_are_never_skipped(AppKind kind) =>
        Assert.Null(Restore.ShouldSkip(new AppEntry { Kind = kind, ExePath = @"C:\a\app.exe" }, Running));

    static StartupApps.Info Startup(string[] commands, params string[] families) =>
        new(commands, new HashSet<string>(families, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void An_app_in_the_startup_list_is_skipped_even_though_it_is_not_running_yet() =>
        Assert.Equal("Starts itself at sign-in",
            Restore.ShouldSkip(new AppEntry { ExePath = @"C:\b\other.exe" }, new HashSet<string>(),
                Startup(new[] { @"""C:\b\other.exe"" --hidden" })));

    [Fact]
    public void A_store_app_with_an_enabled_startup_task_is_skipped() =>
        Assert.Equal("Starts itself at sign-in",
            Restore.ShouldSkip(new AppEntry { Kind = AppKind.Packaged, ExePath = @"C:\WindowsApps\x\W.exe", Aumid = "Fam.App_abc!App" },
                new HashSet<string>(), Startup(Array.Empty<string>(), "Fam.App_abc")));

    [Fact]
    public void An_app_missing_from_the_startup_list_is_still_restored() =>
        Assert.Null(Restore.ShouldSkip(new AppEntry { ExePath = @"C:\b\other.exe" }, new HashSet<string>(),
            Startup(new[] { @"C:\z\unrelated.exe" })));
}

public class StartupAppsTests
{
    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0 }, false)]   // enabled
    [InlineData(new byte[] { 6, 0, 0, 0 }, false)]   // enabled
    [InlineData(new byte[] { 3, 0, 0, 0 }, true)]    // disabled in Task Manager
    [InlineData(new byte[] { 7, 0, 0, 0 }, true)]    // disabled
    public void Task_Manager_toggle_is_read_from_the_first_byte(byte[] record, bool disabled) =>
        Assert.Equal(disabled, StartupApps.IsDisabledFlag(record));

    [Fact]
    public void No_record_means_enabled() => Assert.False(StartupApps.IsDisabledFlag(null));

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    public void Store_startup_task_state(int state, bool enabled) => Assert.Equal(enabled, StartupApps.IsEnabledTaskState(state));

    [Theory]
    [InlineData(@"""C:\Users\u\AppData\Local\Discord\Update.exe"" --processStart Discord.exe --process-start-args --start-minimized", @"C:\Users\u\AppData\Local\Discord\app-1\Discord.exe", true)]
    [InlineData(@"""C:\Program Files\Wispr\Wispr Flow.exe"" --hidden", @"C:\Program Files\Wispr\Wispr Flow.exe", true)]
    [InlineData(@"C:\tools\NotCode.exe", @"C:\Users\u\AppData\Local\Programs\Code\Code.exe", false)]
    [InlineData(@"""C:\Windows\notepad.exe""", @"C:\Program Files\Sublime Text 3\sublime_text.exe", false)]
    [InlineData("", @"C:\a\app.exe", false)]
    public void Startup_command_is_matched_to_an_executable(string command, string exe, bool expected) =>
        Assert.Equal(expected, StartupApps.CommandLaunches(command, exe));
}
