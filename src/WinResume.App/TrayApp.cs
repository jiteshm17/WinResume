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
    readonly System.Windows.Forms.Timer autoSaveTimer = new();
    // Until the saved session from last time has been restored or declined, an autosave would overwrite it.
    bool sessionPending;

    public TrayApp()
    {
        window = new ShutdownWindow();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Restore last session now", null, (_, _) => AskAndRestore(askFirst: false));
        var pause = new ToolStripMenuItem("Pause (save nothing at shutdown)") { Checked = SessionStore.LoadSettings().Paused, CheckOnClick = true };
        pause.CheckedChanged += (_, _) =>
        {
            SessionStore.SaveSettings(SessionStore.LoadSettings() with { Paused = pause.Checked });
            ApplyAutoSave();
        };
        menu.Items.Add(pause);
        var terminal = new ToolStripMenuItem("Include Windows Terminal tabs") { Checked = SessionStore.LoadSettings().RestoreTerminal, CheckOnClick = true };
        terminal.CheckedChanged += (_, _) => SessionStore.SaveSettings(SessionStore.LoadSettings() with { RestoreTerminal = terminal.Checked });
        menu.Items.Add(terminal);
        var autoSave = new ToolStripMenuItem("Crash protection (save every few minutes)") { Checked = SessionStore.LoadSettings().AutoSave, CheckOnClick = true };
        autoSave.CheckedChanged += (_, _) =>
        {
            SessionStore.SaveSettings(SessionStore.LoadSettings() with { AutoSave = autoSave.Checked });
            ApplyAutoSave();
        };
        menu.Items.Add(autoSave);
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = IsAutostart(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => SetAutostart(autostart.Checked);
        menu.Items.Add(autostart);
        menu.Items.Add("Open data folder", null, (_, _) => System.Diagnostics.Process.Start("explorer.exe", SessionStore.Folder));
        menu.Items.Add("Exit", null, (_, _) =>
        {
            SessionStore.DeleteAutosave(); // quitting on purpose: a leftover autosave would be offered as a crash recovery
            Application.Exit();
        });

        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "WinResume", ContextMenuStrip = menu, Visible = true };

        // Right after sign-in many apps are still starting themselves, so wait a little before offering to restore.
        signInTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        signInTimer.Tick += (_, _) => { signInTimer.Stop(); AskAndRestore(askFirst: true); };
        var saved = SessionStore.Load();
        SessionStore.Log(saved is null ? "started; no saved session" : $"started; saved session found: {SessionFlow.Describe(saved)}");
        if (saved is not null) { sessionPending = true; signInTimer.Start(); }

        autoSaveTimer.Tick += (_, _) => AutoSaveTick();
        ApplyAutoSave();
    }

    /// <summary>Starts or stops the background save to match the settings (and re-reads the interval).</summary>
    void ApplyAutoSave()
    {
        var settings = SessionStore.LoadSettings();
        if (!settings.AutoSave || settings.Paused)
        {
            autoSaveTimer.Stop();
            if (!sessionPending) SessionStore.DeleteAutosave(); // off or paused means nothing should stay recorded
            return;
        }
        autoSaveTimer.Interval = Math.Max(1, settings.AutoSaveMinutes) * 60_000;
        if (!sessionPending && !autoSaveTimer.Enabled)
        {
            autoSaveTimer.Start();
            SessionStore.Log($"crash protection on: saving every {settings.AutoSaveMinutes} min");
        }
    }

    void AutoSaveTick()
    {
        var settings = SessionStore.LoadSettings();
        autoSaveTimer.Interval = Math.Max(1, settings.AutoSaveMinutes) * 60_000;
        if (!settings.AutoSave || settings.Paused) return;
        Task.Run(() => SessionFlow.AutoSave(settings, SessionStore.Log)); // MTA thread pool, off the UI thread
    }

    /// <summary>The saved session has been dealt with, so background saving may begin.</summary>
    void SessionHandled()
    {
        sessionPending = false;
        ApplyAutoSave();
    }

    void AskAndRestore(bool askFirst)
    {
        var snapshot = SessionStore.Load();
        if (snapshot is null)
        {
            SessionHandled();
            if (!askFirst) MessageBox.Show("There is no saved session.", "WinResume");
            return;
        }
        if (askFirst)
        {
            SessionStore.Log("asking whether to restore");
            var answer = MessageBox.Show(
                $"Restore {SessionFlow.Describe(snapshot)} from before you shut down?",
                "WinResume", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) { SessionStore.Delete(); SessionStore.Log("restore declined; session deleted"); SessionHandled(); return; }
        }
        // Restoring drives other apps' windows, so keep it off the UI thread.
        Task.Run(() =>
        {
            try
            {
                var summary = SessionFlow.RestoreSession(snapshot, SessionStore.LoadSettings(), SessionStore.Log);
                // a dialog like the restore prompt, so it stays until read (a tray balloon can vanish or be suppressed)
                window.BeginInvoke(() => { SessionHandled(); ShowSummary(summary); });
            }
            catch (Exception e)
            {
                SessionStore.Log("restore failed: " + e);
                window.BeginInvoke(SessionHandled);
            }
        });
    }

    static void ShowSummary(RestoreSummary summary)
    {
        if (summary.NeedAttention == 0)
        {
            MessageBox.Show(summary.Message, "WinResume", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var answer = MessageBox.Show(summary.Message + "\n\nOpen the log file?", "WinResume",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer == DialogResult.Yes)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SessionStore.LogFile) { UseShellExecute = true });
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
        autoSaveTimer.Dispose();
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
            SessionFlow.ShutdownCancelled();
            SessionStore.Log("shutdown was cancelled; snapshot deleted");
        }
        base.WndProc(ref m);
    }

    static void OnSessionEnding(long flags)
    {
        var settings = SessionStore.LoadSettings();
        SessionStore.Log($"session ending (flags 0x{flags:X}); paused={settings.Paused}");
        if (settings.Paused) { SessionStore.DeleteAutosave(); return; }

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
