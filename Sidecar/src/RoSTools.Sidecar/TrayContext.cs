using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading;
using RoSTools.Sidecar.Core;

namespace RoSTools.Sidecar;

/// <summary>
/// The tray icon and everything hanging off it. Updates are installed silently by
/// design - no balloons - so this class is also the entire status surface.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayContext : ApplicationContext
{
    private readonly SettingsStore _store = new();
    private readonly GuildDataClient _client = new();
    private readonly UpdateService _updates;
    private readonly PollService _poll;

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _checkItem;
    private readonly Control _marshal = new();

    private readonly RegisteredWaitHandle? _showSettingsWait;
    private readonly EventWaitHandle? _showSettingsEvent;

    private SettingsForm? _settingsForm;
    private bool _disposed;

    public TrayContext(EventWaitHandle? showSettingsEvent = null)
    {
        Paths.EnsureStateDirectory();
        _store.Load();

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

        if (!_store.Current.FirstRunCompleted)
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
        var settings = _store.Current;

        if (result?.IsFailure == true || (result is null && settings.LastError is not null))
        {
            var message = result?.Message ?? settings.LastError!;
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

        _statusItem.Text = summary;
        SetIcon(TrayState.Idle, Truncate($"RoS-Tools Sidecar\n{summary}", 127));
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
        var folder = _store.Current.AddOnPath;
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

        _settingsForm = new SettingsForm(_store, () => _poll.CheckNowAsync(force: true));
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
        _icon.Visible = false;
        ExitThread();
    }

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

    // ------------------------------------------------------------------
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            _showSettingsWait?.Unregister(null);
            _showSettingsEvent?.Dispose();

            _poll.CheckStarted -= OnCheckStarted;
            _poll.CheckCompleted -= OnCheckCompleted;
            _poll.DisposeAsync().AsTask().GetAwaiter().GetResult();

            _icon.Visible = false;
            _icon.Dispose();
            _client.Dispose();
            _settingsForm?.Dispose();
            _marshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
