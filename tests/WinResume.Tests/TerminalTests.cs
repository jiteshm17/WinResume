using WinResume.Core.Terminal;

namespace WinResume.Tests;

public class PythonEnvironmentTests
{
    static Dictionary<string, string> Vars(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Venv_is_detected_from_VIRTUAL_ENV() =>
        Assert.Equal((EnvKind.Venv, @"C:\p\.venv"), PythonEnvironment.FromVariables(Vars(("VIRTUAL_ENV", @"C:\p\.venv"))));

    [Fact]
    public void Named_conda_environment_is_detected() =>
        Assert.Equal((EnvKind.Conda, "ml"), PythonEnvironment.FromVariables(Vars(("CONDA_DEFAULT_ENV", "ml"))));

    [Fact]
    public void Conda_base_is_not_worth_restoring() =>
        Assert.Equal((EnvKind.None, (string?)null), PythonEnvironment.FromVariables(Vars(("CONDA_DEFAULT_ENV", "base"))));

    [Fact]
    public void No_variables_means_no_environment() =>
        Assert.Equal((EnvKind.None, (string?)null), PythonEnvironment.FromVariables(Vars()));

    [Fact]
    public void Venv_wins_when_both_are_set() =>
        Assert.Equal(EnvKind.Venv, PythonEnvironment.FromVariables(Vars(("VIRTUAL_ENV", @"C:\p\.venv"), ("CONDA_DEFAULT_ENV", "ml"))).Kind);
}

public class DirectoryChooserTests
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Hook_beats_everything() =>
        Assert.Equal((@"C:\real", DirectorySource.Hook),
            DirectoryChooser.Choose(@"C:\real", @"C:\child", @"C:\other\.venv", Home));

    [Fact]
    public void Child_process_directory_beats_the_stale_process_directory() =>
        Assert.Equal((@"C:\proj", DirectorySource.ChildProcess),
            DirectoryChooser.Choose(null, @"C:\proj", null, Home));

    [Fact]
    public void Venv_parent_is_used_when_the_shell_still_reports_the_home_folder() =>
        Assert.Equal((@"C:\proj", DirectorySource.VenvParent),
            DirectoryChooser.Choose(null, null, @"C:\proj\.venv", Home));

    [Fact]
    public void Venv_parent_is_not_used_when_the_shell_reports_a_real_directory() =>
        Assert.Equal((@"D:\work", DirectorySource.Process),
            DirectoryChooser.Choose(null, null, @"C:\proj\.venv", @"D:\work"));

    [Fact]
    public void Falls_back_to_the_process_directory_and_trims_the_slash() =>
        Assert.Equal((@"D:\work", DirectorySource.Process),
            DirectoryChooser.Choose(null, null, null, @"D:\work\"));
}

public class CommandHistoryTests
{
    [Fact]
    public void Full_venv_python_path_becomes_plain_python() =>
        Assert.Equal(@"python .\viewer_server.py",
            CommandHistory.Friendly(@"""C:\p\.venv/Scripts\python.exe"" .\viewer_server.py", @"C:\p\.venv"));

    [Fact]
    public void Program_outside_a_venv_keeps_its_name_and_arguments() =>
        Assert.Equal("node server.js --port 80",
            CommandHistory.Friendly(@"""C:\Program Files\nodejs\node.exe"" server.js --port 80", null));

    [Fact]
    public void Bare_program_has_no_trailing_space() =>
        Assert.Equal("python", CommandHistory.Friendly(@"""C:\p\.venv\Scripts\python.exe""", @"C:\p\.venv"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Windows\System32\conhost.exe 0xffffffff")]
    public void Nothing_worth_remembering_gives_null(string? commandLine) =>
        Assert.Null(CommandHistory.Friendly(commandLine, null));

    [Fact]
    public void History_script_escapes_single_quotes() =>
        Assert.Contains("'echo ''hi'''", CommandHistory.AddToHistoryScript("echo 'hi'"));

    [Fact]
    public void History_script_does_not_run_the_command() =>
        Assert.DoesNotContain("Invoke-Expression", CommandHistory.AddToHistoryScript("rm -rf x"));
}

public class TerminalRestoreTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "winresume-tests-" + Guid.NewGuid().ToString("N"));

    public TerminalRestoreTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "proj", ".venv", "Scripts"));
        File.WriteAllText(Path.Combine(root, "proj", ".venv", "Scripts", "Activate.ps1"), "");
        File.WriteAllText(Path.Combine(root, "proj", ".venv", "Scripts", "activate.bat"), "");
    }

    public void Dispose() => Directory.Delete(root, true);

    const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";

    [Fact]
    public void Venv_tab_activates_its_environment_in_its_directory()
    {
        var venv = Path.Combine(root, "proj", ".venv");
        var snapshot = new TerminalSnapshot
        {
            Tabs = { new TerminalTab { ShellPath = Pwsh, Directory = Path.Combine(root, "proj"), EnvKind = EnvKind.Venv, EnvPath = venv } },
        };
        var line = Assert.Single(TerminalRestore.BuildCommandLines(snapshot));
        Assert.StartsWith("-w new new-tab", line);
        Assert.Contains($"-d \"{Path.Combine(root, "proj")}\"", line);
        Assert.Contains("-NoExit -Command", line);
        Assert.Contains("Activate.ps1", line);
    }

    [Fact]
    public void Plain_tab_just_opens_the_shell_in_its_directory()
    {
        var snapshot = new TerminalSnapshot { Tabs = { new TerminalTab { ShellPath = Pwsh, Directory = root } } };
        var line = Assert.Single(TerminalRestore.BuildCommandLines(snapshot));
        Assert.EndsWith("-NoExit", line);
        Assert.DoesNotContain("-Command", line);
    }

    [Fact]
    public void Missing_environment_folder_is_skipped_instead_of_failing()
    {
        var tab = new TerminalTab { ShellPath = Pwsh, EnvKind = EnvKind.Venv, EnvPath = Path.Combine(root, "gone", ".venv") };
        Assert.Null(TerminalRestore.ActivationCommand(tab));
    }

    [Fact]
    public void Missing_directory_is_left_out_so_the_tab_still_opens()
    {
        var snapshot = new TerminalSnapshot { Tabs = { new TerminalTab { ShellPath = Pwsh, Directory = Path.Combine(root, "deleted") } } };
        Assert.DoesNotContain("-d ", Assert.Single(TerminalRestore.BuildCommandLines(snapshot)));
    }

    [Fact]
    public void Each_window_is_one_command_and_tabs_keep_their_order()
    {
        var snapshot = new TerminalSnapshot
        {
            Tabs =
            {
                new TerminalTab { ShellPath = Pwsh, Directory = root, WindowId = 1, Order = 1 },
                new TerminalTab { ShellPath = Pwsh, Directory = root, WindowId = 2, Order = 0 },
                new TerminalTab { ShellPath = Pwsh, Directory = Path.Combine(root, "proj"), WindowId = 1, Order = 0 },
            },
        };
        var lines = TerminalRestore.BuildCommandLines(snapshot);
        Assert.Equal(2, lines.Count);
        var first = lines[0];
        Assert.True(first.IndexOf(Path.Combine(root, "proj"), StringComparison.Ordinal) <
                    first.LastIndexOf("-d \"" + root + "\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Cmd_tabs_use_the_batch_activation_script()
    {
        var tab = new TerminalTab { ShellPath = @"C:\Windows\System32\cmd.exe", EnvKind = EnvKind.Venv, EnvPath = Path.Combine(root, "proj", ".venv") };
        Assert.EndsWith("activate.bat\"", TerminalRestore.ActivationCommand(tab));
    }

    [Fact]
    public void Terminal_snapshot_round_trips_through_json()
    {
        var snapshot = new TerminalSnapshot
        {
            Tabs = { new TerminalTab { ShellPath = Pwsh, Directory = root, EnvKind = EnvKind.Conda, EnvPath = "ml", DirectorySource = DirectorySource.Hook } },
        };
        Assert.Equal(snapshot.Tabs[0], TerminalSnapshot.FromJson(snapshot.ToJson()).Tabs[0]);
    }
}
