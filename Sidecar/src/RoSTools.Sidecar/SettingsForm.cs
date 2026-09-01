using System.Globalization;
using System.Runtime.Versioning;
using RoSTools.Sidecar.Core;

namespace RoSTools.Sidecar;

/// <summary>
/// Hand-built layout rather than a .Designer.cs file: it is one small form, and
/// keeping it as plain C# means the whole app stays reviewable as source, which
/// the addon policy asks for anyway.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsForm : Form
{
    private readonly SettingsStore _store;
    private readonly Func<Task<UpdateResult>> _checkNow;
    private readonly Action? _openConsole;

    private readonly TextBox _addOnPath = new();

    /// <summary>
    /// What auto-detect put in the path box, when the setting was empty. Non-null
    /// only while the box still shows exactly that, so Save can leave the setting on
    /// auto-detect instead of pinning it.
    /// </summary>
    private string? _autoDetected;
    private readonly Label _addOnStatus = new();
    private readonly NumericUpDown _interval = new();
    private readonly CheckBox _autoStart = new();
    private readonly TextBox _dataUrl = new();
    private readonly Label _lastResult = new();
    private readonly Button _checkButton = new();

    public SettingsForm(SettingsStore store, Func<Task<UpdateResult>> checkNow, Action? openConsole = null)
    {
        _store = store;
        _checkNow = checkNow;
        _openConsole = openConsole;

        Text = "RoS-Tools Sidecar";
        Icon = TrayIcons.For(TrayState.Idle);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        SizeGripStyle = SizeGripStyle.Show;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(660, 560);
        MinimumSize = new Size(600, 520);
        Padding = new Padding(14);

        BuildLayout();
        LoadFromSettings();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            AutoSize = false,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildAddOnGroup(), 0, 0);
        root.Controls.Add(BuildScheduleGroup(), 0, 1);
        root.Controls.Add(BuildAdvancedGroup(), 0, 2);
        root.Controls.Add(BuildStatusGroup(), 0, 3);
        root.Controls.Add(BuildButtons(), 0, 4);

        Controls.Add(root);
    }

    private Control BuildAddOnGroup()
    {
        var group = new GroupBox
        {
            Text = "Addon folder",
            Dock = DockStyle.Top,
            Height = 112,
            Padding = new Padding(10, 6, 10, 10),
        };

        // A grid rather than absolute bounds: the window is resizable, and the
        // Browse button has to stay pinned to the right edge while the path box
        // takes the slack.
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _addOnPath.Dock = DockStyle.Top;
        _addOnPath.Margin = new Padding(2, 4, 6, 4);
        _addOnPath.TextChanged += (_, _) => UpdateAddOnStatus();

        var browse = new Button { Text = "Browse...", Width = 100, Margin = new Padding(0, 3, 2, 4) };
        browse.Click += (_, _) => Browse();

        _addOnStatus.Dock = DockStyle.Fill;
        _addOnStatus.AutoSize = false;
        _addOnStatus.Margin = new Padding(2, 4, 2, 0);

        grid.Controls.Add(_addOnPath, 0, 0);
        grid.Controls.Add(browse, 1, 0);
        grid.Controls.Add(_addOnStatus, 0, 1);
        grid.SetColumnSpan(_addOnStatus, 2);

        group.Controls.Add(grid);
        return group;
    }

    private Control BuildScheduleGroup()
    {
        var group = new GroupBox
        {
            Text = "Schedule",
            Dock = DockStyle.Top,
            Height = 92,
            Padding = new Padding(10),
        };

        var label = new Label { Text = "Check every", Left = 12, Top = 30, Width = 80, AutoSize = true };

        _interval.SetBounds(96, 26, 60, 23);
        _interval.Minimum = SidecarSettings.MinimumPollHours;
        _interval.Maximum = SidecarSettings.MaximumPollHours;

        var hours = new Label
        {
            Text = "hours  (the roster is regenerated once a day)",
            Left = 164,
            Top = 30,
            AutoSize = true,
        };

        _autoStart.Text = "Start with Windows";
        _autoStart.SetBounds(12, 58, 240, 22);

        group.Controls.Add(label);
        group.Controls.Add(_interval);
        group.Controls.Add(hours);
        group.Controls.Add(_autoStart);
        return group;
    }

    private Control BuildAdvancedGroup()
    {
        var group = new GroupBox
        {
            Text = "Data source",
            Dock = DockStyle.Top,
            Height = 100,
            Padding = new Padding(10, 6, 10, 10),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _dataUrl.Dock = DockStyle.Top;
        _dataUrl.Margin = new Padding(2, 4, 2, 4);

        var note = new Label
        {
            Text = "Roster data comes from the Blizzard Community API, exported daily by the "
                 + "RoS-Tools guild-data workflow. The sidecar never contacts Blizzard directly.",
            Dock = DockStyle.Fill,
            Margin = new Padding(2, 4, 2, 0),
            ForeColor = SystemColors.GrayText,
        };

        grid.Controls.Add(_dataUrl, 0, 0);
        grid.Controls.Add(note, 0, 1);

        group.Controls.Add(grid);
        return group;
    }

    private Control BuildStatusGroup()
    {
        var group = new GroupBox
        {
            Text = "Last check",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
        };

        _lastResult.Dock = DockStyle.Fill;
        _lastResult.Padding = new Padding(4, 8, 4, 4);

        group.Controls.Add(_lastResult);
        return group;
    }

    private Control BuildButtons()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };

        var close = new Button { Text = "Close", Width = 90, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();

        var save = new Button { Text = "Save", Width = 90 };
        save.Click += (_, _) => SaveSettings(announce: true);

        _checkButton.Text = "Check now";
        _checkButton.Width = 100;
        _checkButton.Click += (_, _) => _ = CheckNowAsync();

        var logs = new Button { Text = "Open log folder", Width = 120 };
        logs.Click += (_, _) =>
        {
            Directory.CreateDirectory(Log.Directory);
            TrayContext.OpenInExplorer(Log.Directory);
        };

        row.Controls.Add(close);
        row.Controls.Add(save);
        row.Controls.Add(_checkButton);
        row.Controls.Add(logs);

        if (_openConsole is not null)
        {
            var console = new Button { Text = "Data console", Width = 110 };
            console.Click += (_, _) => _openConsole();
            row.Controls.Add(console);
        }

        CancelButton = close;
        return row;
    }

    // ------------------------------------------------------------------
    private void LoadFromSettings()
    {
        // Snapshot rather than Current: the poll thread can be inside
        // SettingsStore.Update on the same instance while this window is being built.
        var settings = _store.Snapshot();

        // Remember whether the box is showing a real setting or just what auto-detect
        // found, so Save can tell the difference. Writing the pre-filled value back
        // as an explicit AddOnPath turned a self-correcting setting into a pinned one
        // for anyone who opened this window to change something else entirely - and
        // "Check now" saves first, so it took one click. They would only find out
        // after moving their WoW install, when every check failed against a folder
        // they never chose.
        _autoDetected = settings.AddOnPath is null ? AddOnLocator.FindAddOnFolder() : null;
        _addOnPath.Text = settings.AddOnPath ?? _autoDetected ?? string.Empty;
        _interval.Value = settings.EffectivePollHours;
        _dataUrl.Text = settings.DataUrl;
        _autoStart.Checked = AutoStart.IsEnabled();

        UpdateAddOnStatus();
        ShowStoredResult();
    }

    private void UpdateAddOnStatus()
    {
        var path = _addOnPath.Text.Trim();

        if (path.Length == 0)
        {
            _addOnStatus.ForeColor = SystemColors.GrayText;
            _addOnStatus.Text = "Empty - the sidecar will auto-detect your WoW install on each check.";
            return;
        }

        if (AddOnLocator.LooksLikeAddOnFolder(path))
        {
            _addOnStatus.ForeColor = Color.FromArgb(0, 110, 60);
            _addOnStatus.Text = $"Looks right. Roster will be written to {AddOnLocator.DataFileFor(path)}";
            return;
        }

        _addOnStatus.ForeColor = Color.FromArgb(160, 40, 30);
        _addOnStatus.Text = $"No {AddOnLocator.TocFileName} in this folder. "
                          + "Pick the RoS-Tools folder itself, not Interface\\AddOns.";
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select your RoS-Tools addon folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        var current = _addOnPath.Text.Trim();
        if (Directory.Exists(current))
        {
            dialog.SelectedPath = current;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _addOnPath.Text = dialog.SelectedPath;
        }
    }

    /// <summary>
    /// Saves, and reports its own failures. Every call inside <see cref="Save"/>
    /// currently swallows its own errors, so nothing concrete throws today - but the
    /// two callers make an escape invisible if one ever does: Save's Click handler
    /// would hand it to <see cref="Application.ThreadException"/>, which only logs,
    /// and "Check now" discards the Task it came from. Either way the user sees a
    /// window that looks saved and settings that are not.
    /// </summary>
    private bool SaveSettings(bool announce)
    {
        try
        {
            return Save(announce);
        }
        catch (Exception ex)
        {
            Log.Error("could not save settings", ex);

            _lastResult.ForeColor = Color.FromArgb(160, 40, 30);
            _lastResult.Text = $"Settings were NOT saved: {ex.Message}";

            MessageBox.Show(
                this,
                $"The settings could not be saved:\n\n{ex.Message}\n\n"
                + $"Nothing was changed. The log folder is {Log.Directory}.",
                "RoS-Tools Sidecar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            return false;
        }
    }

    private bool Save(bool announce)
    {
        var path = _addOnPath.Text.Trim();

        if (path.Length > 0 && !AddOnLocator.LooksLikeAddOnFolder(path))
        {
            MessageBox.Show(
                this,
                $"That folder has no {AddOnLocator.TocFileName} in it, so the addon would never read "
                + "what the sidecar writes.\n\nPick the RoS-Tools folder itself, or clear the box to "
                + "let the sidecar auto-detect.",
                "RoS-Tools Sidecar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var url = _dataUrl.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            MessageBox.Show(
                this,
                "The data source must be an https:// URL.",
                "RoS-Tools Sidecar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        // An untouched auto-detected value stays auto-detected.
        var stillAutoDetected =
            _autoDetected is not null &&
            string.Equals(path, _autoDetected, StringComparison.OrdinalIgnoreCase);

        // Explicitly rooted, and resolved out here rather than inside the mutation.
        // A relative path would make the destination follow whatever directory the
        // process happened to start in; resolving it before the lock means a path
        // GetFullPath refuses cannot leave Current half-written and unsaved.
        var rooted = path.Length == 0 || stillAutoDetected ? null : Path.GetFullPath(path);

        _store.Update(s =>
        {
            s.AddOnPath = rooted;
            s.DataUrl = url;
            s.PollIntervalHours = (int)_interval.Value;
            s.StartWithWindows = _autoStart.Checked;

            // No ETag invalidation needed here any more. The cache is keyed by
            // destination and carries the URL it came from plus the generated_epoch
            // of the file actually installed there, so changing either the folder or
            // the URL simply misses the cache. That is the fix for the older bug
            // where a cached ETag from one addon folder answered a second one with a
            // 304 and "Already up to date" over stale data.
        });

        AutoStart.Set(_autoStart.Checked);
        Log.Info("settings saved.");

        if (announce)
        {
            _lastResult.ForeColor = SystemColors.ControlText;
            _lastResult.Text = "Settings saved.";
        }

        return true;
    }

    private async Task CheckNowAsync()
    {
        // Saving first is deliberate - a check has to run against what the window
        // shows - but it must not be able to throw out here, where the caller has
        // discarded the Task and nothing would ever observe it. SaveSettings owns
        // its own failures and answers false.
        if (!SaveSettings(announce: false))
        {
            return;
        }

        try
        {
            _checkButton.Enabled = false;
            _lastResult.ForeColor = SystemColors.ControlText;
            _lastResult.Text = "Checking...";

            await _checkNow().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("check from settings window failed", ex);
            ReportResult(new UpdateResult(
                UpdateOutcome.Failed, ex.Message, 0, null, DateTimeOffset.UtcNow));
        }
        finally
        {
            if (!IsDisposed)
            {
                _checkButton.Enabled = true;
            }
        }
    }

    /// <summary>Called by <see cref="TrayContext"/> when any check finishes.</summary>
    public void ReportResult(UpdateResult result)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => ReportResult(result));
            return;
        }

        _checkButton.Enabled = true;
        _lastResult.ForeColor = result.IsFailure
            ? Color.FromArgb(160, 40, 30)
            : SystemColors.ControlText;

        _lastResult.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0:t}  {1}",
            result.AtUtc.ToLocalTime(),
            result.Message);
    }

    private void ShowStoredResult()
    {
        var settings = _store.Snapshot();

        // A load failure is sticky and lives on the store, not on the settings
        // instance: UpdateService.Record() nulls Current.LastError on every
        // successful check, so a settings file we could not read would stop
        // being mentioned here about a minute after startup. It outranks a
        // check result, because nothing typed into this window will persist
        // while it stands.
        var failure = _store.LoadFailure ?? settings.LastError;
        if (failure is not null)
        {
            _lastResult.ForeColor = Color.FromArgb(160, 40, 30);
            _lastResult.Text = failure;
            return;
        }

        _lastResult.ForeColor = SystemColors.ControlText;

        _lastResult.Text = settings.LastUpdateUtc is { } updated
            ? string.Format(
                CultureInfo.CurrentCulture,
                "{0} characters, exported {1}.\nInstalled {2:g} local time.",
                settings.LastEntryCount,
                settings.LastGeneratedAt ?? "unknown",
                updated.ToLocalTime())
            : "No roster installed yet.";
    }
}
