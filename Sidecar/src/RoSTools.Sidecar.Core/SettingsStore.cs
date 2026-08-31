using System.Text.Json;

namespace RoSTools.Sidecar.Core;

/// <summary>
/// Loads and saves <see cref="SidecarSettings"/> as JSON. A corrupt file is not
/// worth failing over - the same call the PowerShell updater makes - so it is
/// replaced with defaults rather than surfaced as an error.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Lock _gate = new();
    private readonly string _path;

    public SettingsStore(string? path = null) => _path = path ?? Paths.SettingsFile;

    public string Path => _path;

    public SidecarSettings Current { get; private set; } = new();

    public SidecarSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var parsed = JsonSerializer.Deserialize<SidecarSettings>(json, Options);
                    if (parsed is not null)
                    {
                        Current = parsed;
                        return Current;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"settings file unreadable, starting fresh: {ex.Message}");
            }

            Current = new SidecarSettings();
            return Current;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
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
