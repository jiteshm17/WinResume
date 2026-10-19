namespace WinResume.Core;

public static class LaunchCommand
{
    /// <summary>Returns the executable token of a raw Windows command line, without quotes.</summary>
    public static string ExecutableOf(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.Length == 0) return "";
        if (s[0] == '"')
        {
            var close = s.IndexOf('"', 1);
            return close < 0 ? s[1..] : s[1..close];
        }
        var space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    /// <summary>Returns everything after the executable token of a raw Windows command line.</summary>
    public static string ArgumentsOf(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.Length == 0) return "";
        int end;
        if (s[0] == '"')
        {
            var close = s.IndexOf('"', 1);
            end = close < 0 ? s.Length : close + 1;
        }
        else
        {
            var space = s.IndexOf(' ');
            end = space < 0 ? s.Length : space;
        }
        return s[end..].TrimStart();
    }
}
