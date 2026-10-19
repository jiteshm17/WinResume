using System.Runtime.InteropServices;
using WinResume.Core;

namespace WinResume.App;

static class Program
{
    const string MutexName = "WinResume.SingleInstance";

    [STAThread]
    static int Main(string[] args)
    {
        // Test helpers, usable while the tray app is running:
        //   --simulate-shutdown   sends the same messages Windows sends at shutdown to the running app (nothing shuts down)
        //   --restore-now         runs the sign-in restore immediately, without the prompt
        if (args.Contains("--simulate-shutdown")) return SimulateShutdown();
        if (args.Contains("--restore-now")) return RestoreNow();

        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first) return 0; // already running

        ApplicationConfiguration.Initialize();
        using var tray = new TrayApp();
        Application.Run();
        return 0;
    }

    static int SimulateShutdown()
    {
        var window = NativeMethods.FindWindow(null, TrayApp.WindowTitle);
        if (window == IntPtr.Zero) { Console.Error.WriteLine("WinResume is not running"); return 1; }
        NativeMethods.SendMessage(window, NativeMethods.WM_QUERYENDSESSION, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.SendMessage(window, NativeMethods.WM_ENDSESSION, (IntPtr)1, IntPtr.Zero);
        return 0;
    }

    static int RestoreNow()
    {
        var snapshot = SessionStore.Load();
        if (snapshot is null) return 1;
        // not on this STA thread: process and window inspection needs a background (MTA) thread
        Task.Run(() => SessionFlow.RestoreSession(snapshot, SessionStore.LoadSettings(), SessionStore.Log)).Wait();
        return 0;
    }
}

static class NativeMethods
{
    public const int WM_QUERYENDSESSION = 0x0011;
    public const int WM_ENDSESSION = 0x0016;
    public const long ENDSESSION_CRITICAL = 0x40000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? className, string windowTitle);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll")]
    public static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    public static extern bool SetProcessShutdownParameters(uint level, uint flags);
}
