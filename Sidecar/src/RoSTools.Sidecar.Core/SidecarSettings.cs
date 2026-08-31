namespace RoSTools.Sidecar.Core;

/// <summary>
/// What the sidecar knows about one addon folder it installs into.
/// <para>
/// The conditional-request cache is keyed by destination, and every entry carries
/// the <c>generated_epoch</c> of the file this sidecar actually put there. Both
/// halves are load-bearing, and both are lessons from the same bug in the retired
/// PowerShell updater: a single global ETag gated on "the destination
/// file exists" answers a second addon folder - or a destination something else has
/// overwritten or truncated - with a 304 and "Already up to date", leaving stale or
/// broken data installed while reporting success.
/// </para>
/// <para>
/// The rule the state has to satisfy: a cache key must identify <i>what is installed
/// where</i>, never <i>what was last downloaded</i>.
/// </para>
/// </summary>
public sealed class DestinationState
{
    /// <summary>The URL the cached validators came from. A different URL invalidates them.</summary>
    public string? Url { get; set; }

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    /// <summary>
    /// <c>generated_epoch</c> of the file this sidecar installed here, written only
    /// after the install succeeded. A failed install leaves no stamp, so the next run
    /// refetches unconditionally.
    /// </summary>
    public long? Stamp { get; set; }

    public int EntryCount { get; set; }

    public string? GeneratedAt { get; set; }

    public DestinationState Clone() => (DestinationState)MemberwiseClone();
}

public sealed class SidecarSettings
{
    /// <summary>
    /// The <c>guild-data</c> branch is written daily by the "Guild data" workflow
    /// and holds nothing but the payload, so this URL is stable.
    /// <para>
    /// The sidecar never calls the Blizzard API directly. Doing so would put the
    /// client secret on every guildmate's machine and multiply a ~180-call export
    /// by the number of installs. CI is the single API consumer.
    /// </para>
    /// </summary>
    public const string DefaultDataUrl =
        "https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/guild-data/GuildData.lua";

    public const int MinimumPollHours = 1;
    public const int MaximumPollHours = 48;
    public const int DefaultPollHours = 6;

    /// <summary>Explicit addon folder. Null means auto-detect on every check.</summary>
    public string? AddOnPath { get; set; }

    public string DataUrl { get; set; } = DefaultDataUrl;

    public int PollIntervalHours { get; set; } = DefaultPollHours;

    public bool StartWithWindows { get; set; }

    // --- guild identity -------------------------------------------------------

    /// <summary>
    /// The guild this machine carries, learned from the first file it installs and
    /// enforced on every file after. Without it a mistyped data URL installs another
    /// guild's roster; that client then holds the highest <c>generated_epoch</c> in
    /// the guild and every peer wastes its whole anti-entropy window transferring a
    /// snapshot it will reject on identity, silently, forever.
    /// </summary>
    public string? GuildRegion { get; set; }

    public string? GuildRealm { get; set; }

    public string? GuildName { get; set; }

    // --- Blizzard credentials for hand-driven pulls ---------------------------
    //
    // These belong to the web console only. The background poller must never touch
    // them: it reads the published guild-data branch, and CI stays the guild's one
    // scheduled API consumer. See BlizzardApiClient's class remarks.

    public string? BlizzardClientId { get; set; }

    /// <summary>
    /// The client secret, DPAPI-encrypted under the current Windows user. Never the
    /// plaintext, and never returned to the web console - the API reports only
    /// whether a secret is present.
    /// <para>
    /// A blob that will not decrypt is not an error: the settings file has been
    /// copied from another machine or another account, and the user simply re-enters
    /// the secret.
    /// </para>
    /// </summary>
    public string? BlizzardClientSecretProtected { get; set; }

    /// <summary>Region for pulls. Distinct from <see cref="GuildRegion"/>, which is
    /// learned from an installed file and is enforced, not chosen.</summary>
    public string? BlizzardRegion { get; set; }

    // --- per-destination conditional-request cache ----------------------------

    /// <summary>
    /// Keyed by <see cref="Key"/>: the full path, lowercased.
    /// <para>
    /// The lowercasing is what makes the key stable, and it is not a stylistic
    /// choice. A <c>Dictionary</c> initialized with an <c>OrdinalIgnoreCase</c>
    /// comparer loses that comparer the moment System.Text.Json deserializes into
    /// it - the serializer constructs a fresh dictionary with the default ordinal
    /// comparer - so the same folder typed with different capitalisation produced two
    /// entries on disk that then collapsed unpredictably on the next copy. Baking the
    /// normalization into the key makes the comparer irrelevant.
    /// </para>
    /// </summary>
    public Dictionary<string, DestinationState> Destinations { get; set; } = new(StringComparer.Ordinal);

    // --- last-run reporting, shown in the tray tooltip and settings window ----

    public DateTimeOffset? LastCheckUtc { get; set; }

    public DateTimeOffset? LastUpdateUtc { get; set; }

    public int LastEntryCount { get; set; }

    public string? LastGeneratedAt { get; set; }

    /// <summary>Export instant of what is installed, so the tray can age it.</summary>
    public long? LastGeneratedEpoch { get; set; }

    public string? LastError { get; set; }

    public bool FirstRunCompleted { get; set; }

    public int EffectivePollHours =>
        Math.Clamp(PollIntervalHours, MinimumPollHours, MaximumPollHours);

    /// <summary>The learned guild, or null before the first successful install.</summary>
    public GuildIdentity? Guild =>
        !string.IsNullOrWhiteSpace(GuildRegion) &&
        !string.IsNullOrWhiteSpace(GuildRealm) &&
        !string.IsNullOrWhiteSpace(GuildName)
            ? new GuildIdentity(GuildRegion, GuildRealm, GuildName)
            : null;

    public DestinationState? StateFor(string destination)
    {
        // A hand-edited settings file can carry a null here; a missing cache entry
        // just means an unconditional fetch, which is the safe direction.
        Destinations ??= new Dictionary<string, DestinationState>(StringComparer.Ordinal);
        return Destinations.GetValueOrDefault(Key(destination));
    }

    public DestinationState StateForOrNew(string destination)
    {
        Destinations ??= new Dictionary<string, DestinationState>(StringComparer.Ordinal);

        var key = Key(destination);
        if (!Destinations.TryGetValue(key, out var state))
        {
            state = new DestinationState();
            Destinations[key] = state;
        }

        return state;
    }

    private static string Key(string destination)
    {
        try
        {
            return Path.GetFullPath(destination).ToLowerInvariant();
        }
        catch
        {
            // An unusable path still needs a stable key; it will simply never match
            // a real destination, which is the safe direction.
            return destination.ToLowerInvariant();
        }
    }

    /// <summary>
    /// A deep copy. <c>MemberwiseClone</c> alone would share the destination
    /// dictionary with the live settings, which is exactly what a snapshot is for:
    /// a check that takes 40 seconds must not read half of the Settings window's
    /// save partway through.
    /// </summary>
    public SidecarSettings Clone()
    {
        var copy = (SidecarSettings)MemberwiseClone();
        copy.Destinations = new Dictionary<string, DestinationState>(StringComparer.Ordinal);

        foreach (var (key, value) in Destinations ?? [])
        {
            copy.Destinations[key] = value.Clone();
        }

        return copy;
    }
}
