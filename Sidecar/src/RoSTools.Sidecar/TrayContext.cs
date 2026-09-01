using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32;
using RoSTools.Sidecar.Core;
using RoSTools.Sidecar.Core.Blizzard;
using RoSTools.Sidecar.Core.Web;

namespace RoSTools.Sidecar;

/// <summary>
/// The tray icon and everything hanging off it. Updates are installed silently by
/// design - no balloons - so this class is also the entire status surface.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayContext : ApplicationContext
{
    /// <summary>How long shutdown waits for an in-flight console pull to finish.</summary>
    private static readonly TimeSpan PullQuiesceTimeout = TimeSpan.FromSeconds(2);

    private readonly SettingsStore _store = new();
    private readonly GuildDataClient _client = new();
    private readonly UpdateService _updates;
    private readonly PollService _poll;

    /// <summary>
    /// The data console. Started lazily on first use: it opens a listening socket
    /// and mints a session token, and the great majority of runs never touch it.
    /// </summary>
    private readonly Lock _consoleGate = new();
    private PullService? _pulls;
    private ConsoleServer? _console;

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _checkItem;
    private readonly Control _marshal = new();

    private readonly RegisteredWaitHandle? _showSettingsWait;
    private readonly EventWaitHandle? _showSettingsEvent;
    private readonly RegisteredWaitHandle? _quitWait;
    private readonly EventWaitHandle? _quitEvent;

    private SettingsForm? _settingsForm;
    private bool _disposed;

    public TrayContext(EventWaitHandle? showSettingsEvent = null, EventWaitHandle? quitEvent = null)
    {
        Paths.EnsureStateDirectory();
        var settings = _store.Load();

        // Nothing else re-asserts this. StartWithWindows is written when the user
        // ticks the box and never read back, so a Run entry left pointing at an old
        // location - the exe moved by anything other than Install-Sidecar.ps1 - stays
        // broken forever while the box goes on saying it is on.
        AutoStart.Reassert(settings.StartWithWindows);

        _updates = new UpdateService(_store, _client);
        _poll = new PollService(_store, _updates);

        // Forces handle creation on the UI thread so background events have
        // something to marshal through. NotifyIcon itself is not a Control.
        _ = _marshal.Handle;

        _statusItem = new ToolStripMenuItem("Checking...") { Enabled = false };
        _checkItem = new ToolStripMenuItem("Check now", null, (_, _) => _ = CheckNowAsync());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_checkItem);
        menu.Items.Add(new ToolStripMenuItem("Open addon folder", null, (_, _) => OpenAddOnFolder()));
        menu.Items.Add(new ToolStripMenuItem("Data console...", null, (_, _) => OpenConsole()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.For(TrayState.Idle),
            ContextMenuStrip = menu,
            Visible = true,
            Text = "RoS-Tools Sidecar",
        };
        _icon.DoubleClick += (_, _) => ShowSettings();

        _poll.CheckStarted += OnCheckStarted;
        _poll.CheckCompleted += OnCheckCompleted;

        RefreshStatus();

        // A ghost icon after an update is the visible half of this: Install-Sidecar.ps1
        // kills the process, so Quit never runs and the shell keeps painting an icon
        // for a process that is gone. A kill cannot be intercepted from in here, but
        // every softer exit can, and the quit event below gives the installer a way to
        // ask for one.
        Application.ApplicationExit += OnApplicationExit;
        SystemEvents.SessionEnding += OnSessionEnding;

        if (showSettingsEvent is not null)
        {
            _showSettingsEvent = showSettingsEvent;
            _showSettingsWait = ThreadPool.RegisterWaitForSingleObject(
                showSettingsEvent,
                (_, _) => Post(ShowSettings),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }

        if (quitEvent is not null)
        {
            _quitEvent = quitEvent;
            _quitWait = ThreadPool.RegisterWaitForSingleObject(
                quitEvent,
                (_, _) => Post(Quit),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: true);
        }

        if (!settings.FirstRunCompleted)
        {
            // Runs after the message loop is up, so the window has somewhere to sit.
            Post(RunFirstRun);
        }

        _poll.Start();
    }

    // ------------------------------------------------------------------
    // Poll wiring
    // ------------------------------------------------------------------
    private void OnCheckStarted() => Post(() =>
    {
        _checkItem.Enabled = false;
        SetIcon(TrayState.Checking, "RoS-Tools Sidecar - checking...");
        _statusItem.Text = "Checking...";
    });

    private void OnCheckCompleted(UpdateResult result) => Post(() =>
    {
        _checkItem.Enabled = true;
        RefreshStatus(result);
        _settingsForm?.ReportResult(result);
    });

    private async Task CheckNowAsync()
    {
        try
        {
            await _poll.CheckNowAsync(force: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("manual check failed", ex);
        }
    }

    // ------------------------------------------------------------------
    // Status surface
    // ------------------------------------------------------------------
    private void RefreshStatus(UpdateResult? result = null)
    {
        // Snapshot, not Current. This runs on the UI thread while the poll thread is
        // free to be inside SettingsStore.Update mutating the very same instance under
        // its lock. DateTimeOffset? is wider than a word, so LastUpdateUtc and
        // LastCheckUtc can be read torn - a nonsense instant that renders as an absurd
        // "updated 53 years ago", or throws out of the arithmetic below.
        var settings = _store.Snapshot();

        // A failed load first, and independently of the check. It is the more serious
        // of the two - the settings file, and the client secret in it, is either
        // quarantined or being protected by refusing to save - and it does not live in
        // LastError precisely because UpdateService.Record owns that field and nulls
        // it on the first successful check. Read straight off the store, where it is
        // sticky, so the warning cannot be wiped by a poll thirty seconds later.
        var message = _store.LoadFailure
            ?? (result?.IsFailure == true ? result.Message : null)
            ?? (result is null ? settings.LastError : null);

        if (message is not null)
        {
            _statusItem.Text = Truncate(message, 60);
            SetIcon(TrayState.Error, Truncate($"RoS-Tools Sidecar - {message}", 127));
            return;
        }

        string summary;
        if (settings.LastUpdateUtc is { } updated)
        {
            summary = string.Format(
                CultureInfo.CurrentCulture,
                "{0} characters - updated {1}",
                settings.LastEntryCount,
                Relative(updated));
        }
        else
        {
            summary = "No roster installed yet";
        }

        // Nothing above ages. Without the checks below, a poll loop that died on day
        // one renders "180 characters - updated 9 days ago" under a healthy icon for
        // as long as the process runs, and the only evidence is a line in the log.
        var warning = StaleWarning(settings);
        if (warning is not null)
        {
            _statusItem.Text = Truncate($"{summary} - {warning}", 60);
            SetIcon(TrayState.Warning, Truncate($"RoS-Tools Sidecar\n{summary}\n{warning}", 127));
            return;
        }

        _statusItem.Text = summary;
        SetIcon(TrayState.Idle, Truncate($"RoS-Tools Sidecar\n{summary}", 127));
    }

    /// <summary>
    /// Why the user should look at this, or null when there is nothing to say.
    /// Checks that have stopped come first: a roster aging because nothing is
    /// fetching is the same symptom, and naming the cause is more useful.
    /// </summary>
    private static string? StaleWarning(SidecarSettings settings)
    {
        var now = DateTimeOffset.UtcNow;

        if (settings.LastCheckUtc is { } checkedAt)
        {
            // Three intervals: past two consecutive misses, this is not jitter.
            var budget = TimeSpan.FromHours(settings.EffectivePollHours * 3);
            if (now - checkedAt > budget)
            {
                return $"no check since {Relative(checkedAt)}";
            }
        }

        if (settings.LastGeneratedEpoch is { } epoch)
        {
            // Guarded: this value is read straight out of sidecar.json, and
            // RefreshStatus runs from the constructor. An out-of-range number in a
            // corrupt or hand-edited file would otherwise throw before the tray icon
            // is ever created, so the app would appear not to start at all.
            DateTimeOffset exported;
            try
            {
                exported = DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
            catch (ArgumentOutOfRangeException)
            {
                return "the recorded export date is unreadable";
            }

            var age = now - exported;

            // 90 days is Core/Sync.lua's MAX_AGE: past it no client in the guild will
            // accept this roster from a peer, so guild-wide sharing stops dead.
            if (age > TimeSpan.FromDays(90))
            {
                return $"roster is {(int)age.TotalDays} days old and too old to share";
            }

            // Matches the addon's own staleDays default, so the tray and the login
            // line agree about what counts as stale.
            if (age > TimeSpan.FromDays(14))
            {
                return $"roster is {(int)age.TotalDays} days old";
            }
        }

        return null;
    }

    private void SetIcon(TrayState state, string tooltip)
    {
        _icon.Icon = TrayIcons.For(state);

        // NotifyIcon.Text throws above 63 chars on some Windows builds despite the
        // 127-char shell limit; clamp low rather than lose the icon entirely.
        _icon.Text = Truncate(tooltip, 63);
    }

    private static string Relative(DateTimeOffset instant)
    {
        var age = DateTimeOffset.UtcNow - instant;

        if (age < TimeSpan.FromMinutes(2))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            var hours = (int)age.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        var days = (int)age.TotalDays;
        return days == 1 ? "yesterday" : $"{days} days ago";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    // ------------------------------------------------------------------
    // Menu actions
    // ------------------------------------------------------------------
    private void OpenAddOnFolder()
    {
        var folder = _store.Snapshot().AddOnPath;
        if (!AddOnLocator.LooksLikeAddOnFolder(folder))
        {
            folder = AddOnLocator.FindAddOnFolder();
        }

        if (folder is null)
        {
            MessageBox.Show(
                "Could not find an installed RoS-Tools addon. Set the folder in Settings.",
                "RoS-Tools Sidecar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        OpenInExplorer(folder);
    }

    internal static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"could not open {path}", ex);
        }
    }

    /// <summary>
    /// Opens the loopback data console in the default browser.
    /// <para>
    /// The URL carries the session token, which is why it goes straight to the
    /// browser rather than being shown anywhere for copying: a token in a chat
    /// window or a screenshot is a token somebody else can use, and this console can
    /// spend the user's Blizzard quota and install a roster the whole guild adopts.
    /// </para>
    /// </summary>
    internal void OpenConsole()
    {
        string url;

        try
        {
            lock (_consoleGate)
            {
                if (_console is null)
                {
                    _pulls = new PullService(_store);

                    var api = new ConsoleApi(
                        _store,
                        _pulls,
                        DpapiSecretProtector.Default,
                        () => _poll.CheckNowAsync(force: true));

                    _console = new ConsoleServer(api);
                    _console.Start();
                }

                url = _console.Url;
            }
        }
        catch (Exception ex)
        {
            Log.Error("could not start the data console", ex);
            MessageBox.Show(
                $"Could not start the data console: {ex.Message}",
                "RoS-Tools Sidecar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("could not open the browser", ex);
            MessageBox.Show(
                "The console is running but the browser would not open. " +
                $"Open it from the sidecar log: {Log.Directory}",
                "RoS-Tools Sidecar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized)
            {
                _settingsForm.WindowState = FormWindowState.Normal;
            }

            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_store, () => _poll.CheckNowAsync(force: true), OpenConsole);
        _settingsForm.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            RefreshStatus();
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void RunFirstRun()
    {
        var detected = AddOnLocator.FindAddOnFolder();

        var body = detected is null
            ? "The sidecar could not find an installed RoS-Tools addon.\n\n" +
              "Open Settings to point it at your addon folder."
            : $"Found your addon at:\n{detected}\n\n" +
              "The sidecar will keep its guild roster up to date in the background. " +
              "Start it automatically when you sign in to Windows?";

        var buttons = detected is null ? MessageBoxButtons.OK : MessageBoxButtons.YesNo;

        var answer = MessageBox.Show(body, "RoS-Tools Sidecar", buttons, MessageBoxIcon.Information);

        if (detected is not null && answer == DialogResult.Yes)
        {
            AutoStart.Set(true);
            _store.Update(s => s.StartWithWindows = true);
        }

        _store.Update(s => s.FirstRunCompleted = true);

        if (detected is null)
        {
            ShowSettings();
        }
    }

    private void Quit()
    {
        HideIcon();
        ExitThread();
    }

    /// <summary>
    /// The shell only reaps a tray icon when it next notices the owning process is
    /// gone, which for most users is the next time they hover the notification area.
    /// Hiding it explicitly on every exit path we can see is what keeps a stale icon
    /// from outliving the process.
    /// </summary>
    private void HideIcon()
    {
        try
        {
            _icon.Visible = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not hide the tray icon: {ex.Message}");
        }
    }

    private void OnApplicationExit(object? sender, EventArgs e) => HideIcon();

    /// <summary>
    /// Raised on the SystemEvents thread, so the icon is touched through the UI
    /// thread like everything else rather than from underneath it.
    /// </summary>
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Post(HideIcon);

    private void Post(Action action)
    {
        if (_marshal.IsDisposed)
        {
            return;
        }

        try
        {
            _marshal.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not marshal to the UI thread: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the console and waits for what it started, so nothing is left holding a
    /// callback into a service that is about to be disposed.
    /// </summary>
    private void ShutDownConsole()
    {
        ConsoleServer? console;
        PullService? pulls;

        lock (_consoleGate)
        {
            console = _console;
            pulls = _pulls;
            _console = null;
            _pulls = null;
        }

        if (console is null)
        {
            return;
        }

        try
        {
            console.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn($"the console did not shut down cleanly: {ex.Message}");
        }

        // DisposeAsync stops the listener and waits on the accept loop, but requests
        // are dispatched fire-and-forget, so one can still be inside a pull - or
        // inside the check callback - after it returns. Bounded, because a wedged
        // request must not be able to hold Quit open.
        if (pulls is null)
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow + PullQuiesceTimeout;

        while (pulls.IsRunning && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        if (pulls.IsRunning)
        {
            Log.Warn("a console pull was still running at shutdown; carrying on anyway.");
        }
    }

    // ------------------------------------------------------------------
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            Application.ApplicationExit -= OnApplicationExit;
            SystemEvents.SessionEnding -= OnSessionEnding;

            _showSettingsWait?.Unregister(null);
            _showSettingsEvent?.Dispose();
            _quitWait?.Unregister(null);
            _quitEvent?.Dispose();

            _poll.CheckStarted -= OnCheckStarted;
            _poll.CheckCompleted -= OnCheckCompleted;

            // Console first, poll service second, and the order is the fix. OpenConsole
            // hands ConsoleApi the closure `() => _poll.CheckNowAsync(force: true)`, so
            // the console holds a live callback into the poll service for as long as it
            // is answering requests. Disposing the poll service first tears down its
            // CancellationTokenSource and SemaphoreSlim underneath a request that is
            // already in flight, and the ObjectDisposedException surfaces out of the
            // request handler - clicking Quit while the console is mid-check.
            ShutDownConsole();

            try
            {
                _poll.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warn($"the poll service did not shut down cleanly: {ex.Message}");
            }

            HideIcon();
            _icon.Dispose();
            _client.Dispose();
            _settingsForm?.Dispose();
            _marshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
