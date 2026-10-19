using Microsoft.Win32;
using WinResume.Core;

namespace WinResume.App;

/// <summary>
/// A tray icon and an invisible window. The window exists only to receive the "session is ending" message
/// Windows sends when you shut down, restart or sign out; that is the only moment anything is captured.
/// </summary>
sealed class TrayApp : IDisposable
{
    public const string WindowTitle = "WinResume hidden window";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly NotifyIcon icon;
    readonly ShutdownWindow window;
    readonly System.Windows.Forms.Timer signInTimer;

    public TrayApp()
    {
        window = new ShutdownWindow();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Restore last session now", null, (_, _) => AskAndRestore(askFirst: false));
        var pause = new ToolStripMenuItem("Pause (save nothing at shutdown)") { Checked = SessionStore.LoadSettings().Paused, CheckOnClick = true };
        pause.CheckedChanged += (_, _) => SessionStore.SaveSettings(SessionStore.LoadSettings() with { Paused = pause.Checked });
        menu.Items.Add(pause);
        var terminal = new ToolStripMenuItem("Include Windows Terminal tabs") { Checked = SessionStore.LoadSettings().RestoreTerminal, CheckOnClick = true };
        terminal.CheckedChanged += (_, _) => SessionStore.SaveSettings(SessionStore.LoadSettings() with { RestoreTerminal = terminal.Checked });
        menu.Items.Add(terminal);
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = IsAutostart(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => SetAutostart(autostart.Checked);
        menu.Items.Add(autostart);
        menu.Items.Add("Open data folder", null, (_, _) => System.Diagnostics.Process.Start("explorer.exe", SessionStore.Folder));
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "WinResume", ContextMenuStrip = menu, Visible = true };

        // Right after sign-in many apps are still starting themselves, so wait a little before offering to restore.
        signInTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        signInTimer.Tick += (_, _) => { signInTimer.Stop(); AskAndRestore(askFirst: true); };
        var saved = SessionStore.Load();
        SessionStore.Log(saved is null ? "started; no saved session" : $"started; saved session found: {SessionFlow.Describe(saved)}");
        if (saved is not null) signInTimer.Start();
    }

    void AskAndRestore(bool askFirst)
    {
        var snapshot = SessionStore.Load();
        if (snapshot is null)
        {
            if (!askFirst) MessageBox.Show("There is no saved session.", "WinResume");
            return;
        }
        if (askFirst)
        {
            SessionStore.Log("asking whether to restore");
            var answer = MessageBox.Show(
                $"Restore {SessionFlow.Describe(snapshot)} from before you shut down?",
                "WinResume", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) { SessionStore.Delete(); SessionStore.Log("restore declined; session deleted"); return; }
        }
        // Restoring drives other apps' windows, so keep it off the UI thread.
        Task.Run(() =>
        {
            try { SessionFlow.RestoreSession(snapshot, SessionStore.LoadSettings(), SessionStore.Log); }
            catch (Exception e) { SessionStore.Log("restore failed: " + e); }
        });
    }

    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("WinResume") is not null;
    }

    static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (on) key.SetValue("WinResume", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("WinResume", throwOnMissingValue: false);
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        signInTimer.Dispose();
        window.Dispose();
    }
}

/// <summary>Invisible window that handles the end-of-session messages.</summary>
sealed class ShutdownWindow : Form
{
    public ShutdownWindow()
    {
        Text = TrayApp.WindowTitle;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        _ = Handle; // create the window now, without ever showing it
        // Ask Windows to tell this app about the shutdown before the ordinary apps (the default level is 0x280).
        NativeMethods.SetProcessShutdownParameters(0x3FF, 0);
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_QUERYENDSESSION)
        {
            OnSessionEnding((long)m.LParam);
            m.Result = (IntPtr)1; // never veto the shutdown
            return;
        }
        if (m.Msg == NativeMethods.WM_ENDSESSION && m.WParam == IntPtr.Zero)
        {
            // another app cancelled the shutdown: nothing is ending, so the snapshot would be stale
            SessionStore.Delete();
            SessionStore.Log("shutdown was cancelled; snapshot deleted");
        }
        base.WndProc(ref m);
    }

    static void OnSessionEnding(long flags)
    {
        var settings = SessionStore.LoadSettings();
        SessionStore.Log($"session ending (flags 0x{flags:X}); paused={settings.Paused}");
        if (settings.Paused) return;

        // Tell Windows we are busy for a moment so it shows a reason instead of closing everything underneath us.
        NativeMethods.ShutdownBlockReasonCreate(Program_Handle(), "Saving your open apps so they can be restored");
        try
        {
            var critical = (flags & NativeMethods.ENDSESSION_CRITICAL) != 0;
            SessionFlow.CaptureForShutdown(settings, TimeSpan.FromSeconds(critical ? 3 : 8), SessionStore.Log);
        }
        catch (Exception e) { SessionStore.Log("capture failed: " + e); }
        finally { NativeMethods.ShutdownBlockReasonDestroy(Program_Handle()); }
    }

    static IntPtr Program_Handle() => NativeMethods.FindWindow(null, TrayApp.WindowTitle);
}
