using System.Globalization;
using System.Text.Json;

namespace RoSTools.Sidecar.Core;

/// <summary>
/// Loads and saves <see cref="SidecarSettings"/> as JSON. A file that cannot be
/// turned into settings is not worth failing over, so the store falls back to
/// defaults - but it never lets that fallback reach the disk over the original,
/// because falling back is not the end of it: <c>UpdateService.Record</c> calls
/// <see cref="Update"/> on every single check, and that saves. Defaults plus one
/// save is the whole file gone, including
/// <see cref="SidecarSettings.BlizzardClientSecretProtected"/>, which nothing else
/// on the machine holds a copy of. An antivirus or backup agent with the file open
/// <c>FileShare.None</c> at logon is enough to trigger it, and that is transient -
/// the settings were never actually corrupt.
/// <para>
/// Two things protect the original, in this order. The file is moved aside to a
/// <c>.bad</c> copy, and if - and only if - that move fails, saving is suspended
/// until the file can be read again or is gone. The second half is not redundant:
/// the process holding the file <c>FileShare.None</c> is exactly the process that
/// makes the rename fail too, so the quarantine misses precisely the case it was
/// written for.
/// </para>
/// </summary>
public sealed class SettingsStore
{
    /// <summary>
    /// Suffix for the copy kept when a settings file could not be read. Appended to
    /// the whole file name, so <c>sidecar.json</c> becomes <c>sidecar.json.bad</c>.
    /// </summary>
    public const string QuarantineSuffix = ".bad";

    /// <summary>
    /// How many same-second quarantine names to try before giving up. A bound, not a
    /// capacity: unbounded probing here would spin forever on a directory that
    /// answers "exists" to everything, on the failure path of the first thing the
    /// app does. Giving up leaves the file in place and suspends saving, which is
    /// the safe direction.
    /// </summary>
    private const int MaxQuarantineAttempts = 64;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Lock _gate = new();
    private readonly string _path;

    /// <summary>
    /// Set when a load failed and the file could not be moved aside, cleared by a
    /// load that succeeds or by the file going away. While it is set,
    /// <see cref="Save"/> writes nothing.
    /// </summary>
    private bool _savesSuspended;

    public SettingsStore(string? path = null) => _path = path ?? Paths.SettingsFile;

    public string Path => _path;

    public SidecarSettings Current { get; private set; } = new();

    /// <summary>
    /// The failure from the last <see cref="Load"/>, or null if it went fine. Sticky
    /// for the life of the store: only another <see cref="Load"/> clears it.
    /// <para>
    /// Deliberately not <see cref="SidecarSettings.LastError"/>. That field is owned
    /// by the update check - <c>UpdateService.Record</c> assigns it, null included,
    /// on every single check - so a quarantine warning parked there survived the
    /// thirty-second startup delay and was then silently wiped by the first
    /// successful poll. The user's secret is gone and the only notice of it lasted
    /// half a minute. Callers rendering status must OR this in.
    /// </para>
    /// </summary>
    public string? LoadFailure { get; private set; }

    /// <summary>
    /// True while <see cref="Save"/> is refusing to write, because the settings file
    /// on disk could be neither read nor moved aside and is the only copy of itself.
    /// </summary>
    public bool SavesSuspended
    {
        get
        {
            lock (_gate)
            {
                return _savesSuspended;
            }
        }
    }

    /// <summary>
    /// Where a failed load moves the original settings file, when nothing is there
    /// already. A second failure does not reuse it - see <see cref="FreeQuarantinePath"/>.
    /// </summary>
    public string QuarantinePath => _path + QuarantineSuffix;

    /// <summary>
    /// The rename <see cref="Quarantine"/> performs. A test seam, and the only one
    /// in this class: the situation it exists for - the OS refusing to rename a file
    /// another process holds - cannot be produced portably from a unit test, because
    /// <c>FileShare</c> is a no-op on Unix and the test user can rename anything it
    /// can create. Production never replaces this.
    /// </summary>
    internal Action<string, string> MoveAside { get; set; } =
        static (from, to) => File.Move(from, to);

    public SidecarSettings Load()
    {
        lock (_gate)
        {
            string reason;

            try
            {
                if (!File.Exists(_path))
                {
                    // First run. Not a failure: nothing to preserve, nothing to warn
                    // about, and emitting a failure here would put a red tray icon
                    // in front of every new install.
                    Current = new SidecarSettings();
                    Resume("there is no settings file to protect");
                    LoadFailure = null;
                    return Current;
                }

                var json = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize<SidecarSettings>(json, Options);
                if (parsed is not null)
                {
                    Current = parsed;
                    Resume("the settings file was read successfully");
                    LoadFailure = null;
                    return Current;
                }

                reason = "it contained no settings";
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }

            // The file is there and we could not use it. Get it out of the way of the
            // next Save() *before* falling back, so the DPAPI secret and everything
            // else survives a transient read failure.
            var preserved = Quarantine();

            Log.Warn($"settings file unreadable, starting fresh: {reason}");

            Current = new SidecarSettings();

            if (preserved is not null)
            {
                Resume($"the unreadable settings file is now at {preserved}");

                LoadFailure =
                    $"Could not read {_path} ({reason}). It was moved to {preserved} " +
                    "and the sidecar started from defaults; your Blizzard client secret " +
                    "will need re-entering.";
            }
            else
            {
                // Nothing else stands between that file and the next check's Save().
                _savesSuspended = true;

                LoadFailure =
                    $"Could not read {_path} ({reason}), and it could not be moved aside " +
                    "either. The sidecar started from defaults and is not saving settings, " +
                    "so the file is intact; close whatever is holding it and restart the " +
                    "sidecar. Anything changed in the meantime will be forgotten.";
            }

            return Current;
        }
    }

    /// <summary>
    /// Moves an unreadable settings file out of the way of the next save. Returns
    /// where it went, or null if it is still sitting at <see cref="Path"/>.
    /// <para>
    /// Never throws. This runs on the failure path of the very first thing the app
    /// does, and a throw here would turn "your settings reset" into "the sidecar
    /// does not start" - which is strictly worse.
    /// </para>
    /// </summary>
    private string? Quarantine()
    {
        var target = FreeQuarantinePath();

        try
        {
            MoveAside(_path, target);
            return target;
        }
        catch (Exception ex)
        {
            Log.Error($"could not move the unreadable settings file to {target}", ex);
            return null;
        }
    }

    /// <summary>
    /// A quarantine name nothing is using: <c>sidecar.json.bad</c> when it is free,
    /// otherwise a timestamped one beside it.
    /// <para>
    /// Overwriting <c>.bad</c> unconditionally destroyed the secret the quarantine
    /// exists to save, on the second failure rather than the first: the real settings
    /// go to <c>.bad</c>, the next check writes a fresh defaults-only
    /// <c>sidecar.json</c>, and a later read failure then quarantines <i>that</i>
    /// worthless file over the only copy of the original.
    /// </para>
    /// </summary>
    private string FreeQuarantinePath()
    {
        if (!Occupied(QuarantinePath))
        {
            return QuarantinePath;
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var candidate = $"{_path}.{stamp}{QuarantineSuffix}";

        for (var attempt = 2; attempt <= MaxQuarantineAttempts && Occupied(candidate); attempt++)
        {
            candidate = $"{_path}.{stamp}-{attempt}{QuarantineSuffix}";
        }

        return candidate;
    }

    /// <summary>
    /// Whether anything at all is at this path - a directory counts, since a rename
    /// cannot land on one either. A path that cannot be answered for is treated as
    /// occupied: the cost is a different quarantine name, and the alternative is
    /// overwriting something we could not look at.
    /// </summary>
    private static bool Occupied(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not tell whether {path} exists: {ex.Message}");
            return true;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            if (_savesSuspended)
            {
                if (File.Exists(_path))
                {
                    // The whole point. Every check saves, so without this the poll
                    // loop wrote defaults over the still-present good file about
                    // thirty seconds after startup - unprompted, while the tray was
                    // telling the user not to touch anything for exactly that reason.
                    Log.Warn(
                        $"not saving settings: {_path} could not be read or moved aside, and " +
                        "writing would destroy it.");
                    return;
                }

                // Whatever was being protected is no longer there, so there is nothing
                // left to overwrite. This is what stops the suspension being permanent.
                Resume("the unreadable settings file is gone");
            }

            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Write-then-rename, so a crash cannot leave half a settings file.
                var staging = _path + ".tmp";
                File.WriteAllText(staging, JsonSerializer.Serialize(Current, Options));
                File.Move(staging, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("could not save settings", ex);
            }
        }
    }

    /// <summary>Lifts a save suspension, saying why. Callers hold <see cref="_gate"/>.</summary>
    private void Resume(string why)
    {
        if (!_savesSuspended)
        {
            return;
        }

        _savesSuspended = false;
        Log.Info($"saving settings is allowed again: {why}.");
    }

    /// <summary>
    /// A private copy taken under the lock. Cloning <see cref="Current"/> directly
    /// races <see cref="Update"/>, which holds the lock across the whole mutation:
    /// a caller could otherwise observe a half-applied save - the new addon folder
    /// paired with the old data URL - or throw enumerating the destination
    /// dictionary mid-write.
    /// </summary>
    public SidecarSettings Snapshot()
    {
        lock (_gate)
        {
            return Current.Clone();
        }
    }

    /// <summary>Mutate and persist in one step.</summary>
    public void Update(Action<SidecarSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
        }

        Save();
    }
}
