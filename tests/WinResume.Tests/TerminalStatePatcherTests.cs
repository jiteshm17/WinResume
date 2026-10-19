using System.Text.Json.Nodes;
using WinResume.Core.Terminal;

namespace WinResume.Tests;

public class TerminalStatePatcherTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "winresume-patch-" + Guid.NewGuid().ToString("N"));
    const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";

    public TerminalStatePatcherTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "proj", ".venv", "Scripts"));
        File.WriteAllText(Path.Combine(root, "proj", ".venv", "Scripts", "Activate.ps1"), "");
    }

    public void Dispose() => Directory.Delete(root, true);

    static string State(int tabs) => new JsonObject
    {
        ["dismissedBadges"] = new JsonArray(),
        ["persistedWindowLayouts"] = new JsonArray(new JsonObject
        {
            ["tabLayout"] = new JsonArray(
                Enumerable.Range(0, tabs).Select(_ => (JsonNode)new JsonObject
                {
                    ["action"] = "newTab",
                    ["startingDirectory"] = @"C:\Users\x",
                    ["commandline"] = $"\"{Pwsh}\"",
                }).Append(new JsonObject { ["action"] = "switchToTab" }).ToArray()),
        }),
    }.ToJsonString();

    TerminalSnapshot Snapshot(int tabs) => new()
    {
        Tabs = Enumerable.Range(0, tabs).Select(i => new TerminalTab
        {
            ShellPath = Pwsh, Order = i, WindowId = 1,
            Directory = i == 0 ? Path.Combine(root, "proj") : @"C:\Users\x",
            EnvKind = i == 0 ? EnvKind.Venv : EnvKind.None,
            EnvPath = i == 0 ? Path.Combine(root, "proj", ".venv") : null,
        }).ToList(),
    };

    [Fact]
    public void Fills_in_folder_and_activation_for_the_tab_that_needs_it_only()
    {
        var result = TerminalStatePatcher.Patch(State(2), Snapshot(2));
        var change = Assert.Single(result.Changes);
        Assert.Equal((1, 1), (change.Window, change.Tab));
        Assert.Equal(Path.Combine(root, "proj"), change.NewDirectory);
        Assert.Contains("Activate.ps1", change.NewCommandLine);

        var layout = JsonNode.Parse(result.Json!)!["persistedWindowLayouts"]![0]!["tabLayout"]!.AsArray();
        Assert.Equal(Path.Combine(root, "proj"), layout[0]!["startingDirectory"]!.GetValue<string>());
        Assert.Equal(@"C:\Users\x", layout[1]!["startingDirectory"]!.GetValue<string>()); // untouched
    }

    [Fact]
    public void Other_parts_of_the_file_and_the_active_tab_marker_survive()
    {
        var json = TerminalStatePatcher.Patch(State(2), Snapshot(2)).Json!;
        var root = JsonNode.Parse(json)!;
        Assert.NotNull(root["dismissedBadges"]);
        var layout = root["persistedWindowLayouts"]![0]!["tabLayout"]!.AsArray();
        Assert.Equal("switchToTab", layout[^1]!["action"]!.GetValue<string>());
    }

    [Fact]
    public void A_window_whose_tab_count_does_not_match_is_left_alone()
    {
        var result = TerminalStatePatcher.Patch(State(3), Snapshot(2));
        Assert.Empty(result.Changes);
        Assert.Contains(result.Notes, n => n.Contains("left unchanged"));
    }

    [Fact]
    public void A_deleted_folder_is_never_written_into_the_layout()
    {
        var snap = new TerminalSnapshot
        {
            Tabs = { new TerminalTab { ShellPath = Pwsh, WindowId = 1, Directory = Path.Combine(root, "deleted") } },
        };
        var result = TerminalStatePatcher.Patch(State(1), snap);
        Assert.DoesNotContain(result.Changes, c => c.NewDirectory is not null);
    }

    [Fact]
    public void Missing_saved_windows_is_reported_not_crashed_on()
    {
        var result = TerminalStatePatcher.Patch("{}", Snapshot(1));
        Assert.Null(result.Json);
        Assert.NotEmpty(result.Notes);
    }
}
