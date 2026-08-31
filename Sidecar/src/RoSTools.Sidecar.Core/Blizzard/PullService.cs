using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace RoSTools.Sidecar.Core.Blizzard;

public enum PullPhase
{
    Idle,
    Authenticating,
    FetchingRoster,
    FetchingCharacters,
    Writing,
    Done,
    Failed,
}

public sealed record PullProgress(PullPhase Phase, int Done, int Total, string Message)
{
    public static readonly PullProgress Idle = new(PullPhase.Idle, 0, 0, "Nothing pulled yet.");
}

public sealed record RosterDelta(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<ChangedEntry> Changed)
{
    public static readonly RosterDelta Empty = new([], [], []);
}

public sealed record ChangedEntry(string Key, long From, long To);

/// <summary>
/// A completed pull, held in memory until it is installed or replaced.
/// <see cref="StagingPath"/> is a validated file on disk, ready for
/// <see cref="DataInstaller"/>.
/// </summary>
public sealed record PullResult(
    bool Ok,
    string? Error,
    string? StagingPath,
    GuildIdentity? Identity,
    long GeneratedEpoch,
    IReadOnlyList<RosterEntry> Entries,
    int RosterSize,
    int NoProfile,
    IReadOnlyList<string> DroppedKeys,
    GuildDataValidation? Validation,
    RosterDelta Delta,
    DateTimeOffset AtUtc)
{
    public static PullResult Failure(string error) =>
        new(false, error, null, null, 0, [], 0, 0, [], null, RosterDelta.Empty, DateTimeOffset.UtcNow);
}

public sealed record PullRequest(string Region, string Realm, string Guild, int MinLevel = 1);

/// <summary>
/// Drives one hand-triggered pull from the Blizzard API and stages the result.
/// <para>
/// Deliberately separate from <see cref="UpdateService"/>: that class is the
/// scheduled path and reads only the published <c>guild-data</c> branch. This one
/// runs only when a person clicks Pull in the web console, and it never installs
/// anything by itself - <see cref="Install"/> is a second, explicit step, because
/// what gets installed here is announced to the whole guild by
/// <c>Core/Sync.lua</c>.
/// </para>
/// </summary>
public sealed partial class PullService
{
    /// <summary>
    /// Concurrent character requests. Matches the Python exporter's default. The
    /// binding limit is 36,000 calls/hour per credential pair and a full roster is
    /// ~180 calls, so this is about being a polite client, not about staying legal.
    /// </summary>
    private const int Workers = 8;

    private readonly SettingsStore _store;
    private readonly Func<string, BlizzardApiClient> _clientFactory;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public PullService(SettingsStore store, Func<string, BlizzardApiClient>? clientFactory = null)
    {
        _store = store;
        _clientFactory = clientFactory ?? (region => new BlizzardApiClient(region));
    }

    public PullProgress Progress { get; private set; } = PullProgress.Idle;

    /// <summary>
    /// Set synchronously by the caller before the pull task is queued, and cleared
    /// when it finishes.
    /// <para>
    /// <see cref="IsRunning"/> alone cannot be the console's signal: it only becomes
    /// true once the queued task acquires the semaphore, and the page polls the
    /// instant its POST returns. Losing that race made the page stop polling, hide
    /// the progress bar, and render the PREVIOUS pull's roster as if the new one had
    /// finished - complete with a live Install button.
    /// </para>
    /// </summary>
    public bool Starting { get; private set; }

    /// <summary>Called on the request thread, before the pull is queued.</summary>
    public void MarkStarting()
    {
        Starting = true;
        Progress = new PullProgress(PullPhase.Authenticating, 0, 0, "Starting...");
    }

    /// <summary>The last completed pull, or null. Staged but not installed.</summary>
    public PullResult? Last { get; private set; }

    public bool IsRunning => Starting || _oneAtATime.CurrentCount == 0;

    public async Task<PullResult> PullAsync(
        BlizzardCredentials credentials,
        PullRequest request,
        CancellationToken ct = default)
    {
        // Nothing may escape this method. It is started with Task.Run and never
        // awaited, so an exception here is an unobserved fault: Last keeps the
        // PREVIOUS pull, Progress freezes mid-count, IsRunning goes false, and the
        // page then renders that stale roster with a live Install button and no
        // error anywhere. The semaphore acquisition below is itself a throw site -
        // WaitAsync raises OperationCanceledException on an already-cancelled token,
        // outside every catch further in - so the guard has to start here.
        try
        {
            return await PullCoreAsync(credentials, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failure = ex is OperationCanceledException
                ? PullResult.Failure("The pull was cancelled.")
                : PullResult.Failure($"The pull failed: {ex.Message}");

            if (ex is not OperationCanceledException)
            {
                Log.Error("pull failed", ex);
            }

            Starting = false;
            Last = failure;
            Progress = new PullProgress(PullPhase.Failed, 0, 0, failure.Error!);
            return failure;
        }
    }

    private async Task<PullResult> PullCoreAsync(
        BlizzardCredentials credentials,
        PullRequest request,
        CancellationToken ct)
    {
        // A second pull while one is in flight would race on Last and leak the
        // first one's staging file. The console disables the button, but the
        // button is not the enforcement.
        if (!await _oneAtATime.WaitAsync(0, ct).ConfigureAwait(false))
        {
            Starting = false;
            return PullResult.Failure("A pull is already running.");
        }

        try
        {
            var result = await RunAsync(credentials, request, ct).ConfigureAwait(false);

            // Replacing a staged-but-uninstalled pull deletes its file; otherwise
            // every discarded attempt leaves a roster in %TEMP% forever.
            if (Last?.StagingPath is { } previous)
            {
                GuildDataClient.TryDelete(previous);
            }

            Last = result;
            Progress = result.Ok
                ? new PullProgress(PullPhase.Done, result.Entries.Count, result.RosterSize,
                    $"Pulled {result.Entries.Count} characters.")
                : new PullProgress(PullPhase.Failed, 0, 0, result.Error ?? "The pull failed.");

            return result;
        }
        finally
        {
            // Order matters: the semaphore is released last, so IsRunning never dips
            // false between clearing the flag and releasing it.
            Starting = false;
            _oneAtATime.Release();
        }
    }

    private async Task<PullResult> RunAsync(
        BlizzardCredentials credentials,
        PullRequest request,
        CancellationToken ct)
    {
        if (!BlizzardCredentials.IsKnownRegion(request.Region))
        {
            return PullResult.Failure($"'{request.Region}' is not a supported region.");
        }

        var realmSlug = BlizzardApiClient.Slugify(request.Realm);
        var guildSlug = BlizzardApiClient.Slugify(request.Guild);

        if (realmSlug.Length == 0 || guildSlug.Length == 0)
        {
            return PullResult.Failure("Realm and guild are both required.");
        }

        // Checked here rather than after ~180 API calls. Slugify strips apostrophes
        // and collapses whitespace but leaves quotes, backslashes and braces intact,
        // and these values are written into Lua string literals in the meta table.
        // The validator does catch every such file, but it catches it at the end of
        // a full pull and reports it as a writer bug, which it is not.
        foreach (var (field, value) in new[] { ("realm", realmSlug), ("guild", guildSlug) })
        {
            if (!SlugShape().IsMatch(value))
            {
                return PullResult.Failure(
                    $"'{value}' is not a usable {field} name. Use the plain name as it appears " +
                    "in-game -- letters, numbers and spaces.");
            }
        }

        var identity = new GuildIdentity(request.Region, realmSlug, guildSlug);

        // Fail here rather than after ~180 API calls: the installer would refuse this
        // file anyway, and the user should find that out before spending the quota.
        // The installed file's own identity is the fallback, exactly as UpdateService
        // does it. Without it, a fresh sidecar sitting next to an already-installed
        // roster has settings.Guild == null and NO identity check at all -- so one
        // typo in the realm box installs another guild's roster, stamps it with a
        // fresh epoch, and this client then holds the highest epoch in the guild
        // while every peer rejects the snapshot on identity, silently, forever. It
        // also self-locks: the install would teach this machine the wrong guild and
        // the legitimate CI file would be refused from then on.
        var settingsNow = _store.Snapshot();
        var carried = settingsNow.Guild
                      ?? (PullService.InstalledDataFile(settingsNow) is { } existing
                          ? GuildDataValidator.IdentityOf(existing)
                          : null);

        if (carried is not null && !identity.Matches(carried))
        {
            return PullResult.Failure(
                $"This machine carries {carried}, so a pull for {identity} could never be installed. " +
                "Correct the realm and guild above -- they must match the roster this machine " +
                "already has.");
        }

        using var client = _clientFactory(request.Region);

        try
        {
            Progress = new PullProgress(PullPhase.Authenticating, 0, 0, "Signing in to Blizzard...");
            await client.AuthenticateAsync(credentials.ClientId, credentials.ClientSecret, ct)
                .ConfigureAwait(false);

            Progress = new PullProgress(PullPhase.FetchingRoster, 0, 0, $"Fetching the {guildSlug} roster...");
            var roster = await client.GetRosterAsync(realmSlug, guildSlug, ct).ConfigureAwait(false);

            var targets = roster.Where(m => m.Level >= request.MinLevel).ToList();
            if (targets.Count == 0)
            {
                return PullResult.Failure(
                    $"The roster came back with no characters at or above level {request.MinLevel}.");
            }

            Progress = new PullProgress(
                PullPhase.FetchingCharacters, 0, targets.Count,
                $"0 / {targets.Count} characters...");

            var entries = new ConcurrentBag<RosterEntry>();
            var done = 0;
            var missing = 0;

            await Parallel.ForEachAsync(
                targets,
                new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = ct },
                async (member, token) =>
                {
                    int? ilvl = null;
                    try
                    {
                        ilvl = await client.GetItemLevelAsync(member.RealmSlug, member.Name, token)
                            .ConfigureAwait(false);
                    }
                    catch (BlizzardApiException ex)
                    {
                        // One unreachable character must not lose the other 179. The
                        // count is reported, and the shrink guard in Install() is what
                        // stops a pull that lost too many from reaching the guild.
                        Log.Warn($"could not read {member.Key}: {ex.Message}");
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // HttpClient's own timeout. Same treatment as any other failed
                        // character: counted as missing, not fatal to the whole pull.
                        Log.Warn($"timed out reading {member.Key}");
                    }

                    if (ilvl is { } value)
                    {
                        entries.Add(new RosterEntry(member.Key, value));
                    }
                    else
                    {
                        Interlocked.Increment(ref missing);
                    }

                    var completed = Interlocked.Increment(ref done);
                    if (completed % 5 == 0 || completed == targets.Count)
                    {
                        Progress = new PullProgress(
                            PullPhase.FetchingCharacters, completed, targets.Count,
                            $"{completed} / {targets.Count} characters...");
                    }
                }).ConfigureAwait(false);

            var collected = entries.ToList();
            if (collected.Count == 0)
            {
                return PullResult.Failure(
                    "Every character came back without an item level. That usually means the " +
                    "profile namespace is not enabled for this client, or nobody has logged in.");
            }

            Progress = new PullProgress(PullPhase.Writing, collected.Count, targets.Count, "Writing the roster...");

            var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var lua = GuildDataWriter.Render(collected, identity, epoch, out var dropped);

            var staging = Path.Combine(Path.GetTempPath(), $"GuildData-pull-{Guid.NewGuid():N}.lua");
            GuildDataWriter.WriteTo(staging, lua);

            // The same gate the download path goes through, with the same expected
            // identity. If this ever fails, the writer has drifted from the exporter's
            // shape and the bug is here, not in the user's input.
            var check = GuildDataValidator.Validate(staging, carried);
            if (!check.Ok)
            {
                GuildDataClient.TryDelete(staging);
                return PullResult.Failure($"The roster this pull produced is not installable: {check.Reason}");
            }

            // Read back from the validated file rather than reporting `collected`.
            // Render() drops unusable keys and out-of-range item levels, so the
            // pre-drop list overstates what was actually written -- which made the
            // page's own shrink warning disagree with the server's, and hid genuinely
            // removed characters from the review screen that exists to catch exactly
            // that. What the user reviews is now what the file contains.
            var written = (GuildDataValidator.EntriesOf(staging) ?? [])
                .Select(e => new RosterEntry(e.Key, (int)e.Value))
                .ToList();

            var delta = DeltaAgainstInstalled(written);

            Log.Info(
                $"pulled {written.Count} characters for {identity} " +
                $"({missing} without a profile, {dropped.Count} keys dropped)");

            return new PullResult(
                true, null, staging, identity, epoch,
                written.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList(),
                targets.Count, missing, dropped, check, delta, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PullResult.Failure("The pull was cancelled.");
        }
        catch (OperationCanceledException)
        {
            // Not our token: this is HttpClient's 30-second timeout, which surfaces as
            // TaskCanceledException. Letting it escape left Last holding the PREVIOUS
            // pull and Progress frozen mid-count, so the page stopped polling and
            // offered the stale roster for install with no error shown anywhere.
            return PullResult.Failure(
                "A request to Blizzard timed out. Try the pull again -- nothing was installed.");
        }
        catch (BlizzardApiException ex)
        {
            return PullResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("pull failed", ex);
            return PullResult.Failure($"The pull failed: {ex.Message}");
        }
    }

    private RosterDelta DeltaAgainstInstalled(List<RosterEntry> pulled)
    {
        var destination = InstalledDataFile(_store.Snapshot());
        if (destination is null)
        {
            return RosterDelta.Empty;
        }

        var installed = GuildDataValidator.EntriesOf(destination);
        if (installed is null)
        {
            return RosterDelta.Empty;
        }

        var before = installed.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var after = pulled.ToDictionary(e => e.Key, e => (long)e.Ilvl, StringComparer.Ordinal);

        var added = after.Keys.Where(k => !before.ContainsKey(k)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).Order(StringComparer.OrdinalIgnoreCase).ToList();

        var changed = after
            .Where(pair => before.TryGetValue(pair.Key, out var was) && was != pair.Value)
            .Select(pair => new ChangedEntry(pair.Key, before[pair.Key], pair.Value))
            .OrderByDescending(c => Math.Abs(c.To - c.From))
            .ToList();

        return new RosterDelta(added, removed, changed);
    }

    /// <summary>
    /// How much of the installed roster a pull may lose before
    /// <see cref="Install"/> refuses it without an explicit override.
    /// <para>
    /// This is the guard that matters most in this whole feature. A pull that hits a
    /// bad ten minutes at Blizzard - throttling, a batch of 503s, the profile
    /// namespace briefly refusing - comes back with a partial roster that is
    /// perfectly valid Lua and passes every check the validator makes. Installing it
    /// stamps a fresh <c>generated_epoch</c>, which makes it the newest data in the
    /// guild, and <c>Core/Sync.lua</c> then hands that truncated roster to every
    /// peer that asks. The validator cannot catch this: it only ever sees one file,
    /// and one file cannot show that half the guild went missing.
    /// </para>
    /// </summary>
    public const double ShrinkFloor = 0.80;

    public sealed record InstallOutcome(bool Ok, string Message, int Entries, bool NeedsOverride = false);

    /// <summary>
    /// Installs a staged pull. Separate from <see cref="PullAsync"/> on purpose: the
    /// console shows the data first and this only runs on a second, explicit click.
    /// </summary>
    public InstallOutcome Install(bool overrideShrink)
    {
        // Serialized against PullAsync and against another Install. Without this, a
        // pull completing mid-install deletes the staging file between the shrink
        // check and the copy, and two concurrent installs both clear the same guard.
        if (!_oneAtATime.Wait(TimeSpan.FromSeconds(30)))
        {
            return new InstallOutcome(false, "A pull is running. Try again when it finishes.", 0);
        }

        try
        {
            return InstallCore(overrideShrink);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private InstallOutcome InstallCore(bool overrideShrink)
    {
        var result = Last;
        if (result is not { Ok: true, StagingPath: not null })
        {
            return new InstallOutcome(false, "There is no pulled roster to install.", 0);
        }

        if (!File.Exists(result.StagingPath))
        {
            return new InstallOutcome(false, "The pulled roster is no longer on disk. Pull again.", 0);
        }

        var settings = _store.Snapshot();

        var addOnFolder = settings.AddOnPath;
        if (!AddOnLocator.LooksLikeAddOnFolder(addOnFolder))
        {
            addOnFolder = AddOnLocator.FindAddOnFolder();
        }

        if (addOnFolder is null)
        {
            return new InstallOutcome(
                false,
                "Could not find an installed RoS-Tools addon. Set the folder in Settings.",
                0);
        }

        var destination = AddOnLocator.DataFileFor(addOnFolder);

        // Re-validate against the identity as it stands now, not as it stood when the
        // pull ran. A file installed between the two clicks can teach this machine a
        // guild, and this file must still match it.
        // Same fallback as the pre-flight check, and for the same reason: with no
        // learned guild and no fallback there is no identity check here at all.
        var expected = settings.Guild ?? GuildDataValidator.IdentityOf(destination);

        var check = GuildDataValidator.Validate(result.StagingPath, expected);
        if (!check.Ok)
        {
            return new InstallOutcome(false, $"Refused: {check.Reason}", 0);
        }

        // The baseline is a roster the addon could actually load, not merely one whose
        // ilvls table happens to parse: a destination the validator rejects is not
        // data worth protecting, and using its count would refuse a good pull as a
        // "shrink" against a file nobody can read.
        var current = GuildDataValidator.Validate(destination, settings.Guild);
        if (current.Ok && current.Entries > 0 && !overrideShrink)
        {
            var floor = current.Entries * ShrinkFloor;
            if (check.Entries < floor)
            {
                return new InstallOutcome(
                    false,
                    $"This pull has {check.Entries} characters but {current.Entries} are installed -- " +
                    $"a drop of {current.Entries - check.Entries}. Installing it would announce the " +
                    "smaller roster to your whole guild. Pull again, or tick the override if the " +
                    "guild really did shrink.",
                    check.Entries,
                    NeedsOverride: true);
            }
        }

        try
        {
            // Copy, don't move: a failed install should leave the staged pull intact so
            // the console can still show it and the user can retry.
            var staged = Path.Combine(Path.GetTempPath(), $"GuildData-install-{Guid.NewGuid():N}.lua");
            File.Copy(result.StagingPath, staged, overwrite: true);

            DataInstaller.Install(staged, destination);
        }
        catch (Exception ex)
        {
            Log.Error("could not install a pulled roster", ex);
            return new InstallOutcome(false, $"Could not install: {ex.Message}", 0);
        }

        var now = DateTimeOffset.UtcNow;

        _store.Update(s =>
        {
            // The ETag cache describes what the *download* path put here. This file did
            // not come from that URL, so leaving the old validators in place would let
            // the next poll answer 304 over a file the server never sent. Dropping the
            // entry costs one unconditional fetch and keeps the cache's invariant:
            // a key identifies what is installed where, never what was last downloaded.
            s.Destinations?.Remove(DestinationKey(destination));

            if (s.Guild is null && check.Identity is not null)
            {
                s.GuildRegion = check.Identity.Region;
                s.GuildRealm = check.Identity.Realm;
                s.GuildName = check.Identity.Guild;
                Log.Info($"this machine now carries {check.Identity}.");
            }

            s.LastUpdateUtc = now;
            s.LastCheckUtc = now;
            s.LastEntryCount = check.Entries;
            s.LastGeneratedAt = check.GeneratedAt;
            s.LastGeneratedEpoch = check.GeneratedEpoch;
            s.LastError = null;
        });

        Log.Info($"installed {check.Entries} pulled characters to {destination}");

        var message = $"Installed {check.Entries} characters to {destination}.";
        if (check.Warning is not null)
        {
            message += " " + check.Warning;
        }

        return new InstallOutcome(true, message, check.Entries);
    }

    /// <summary>Where the addon's roster lives, or null when the addon cannot be found.</summary>
    public static string? InstalledDataFile(SidecarSettings settings)
    {
        var folder = settings.AddOnPath;
        if (!AddOnLocator.LooksLikeAddOnFolder(folder))
        {
            folder = AddOnLocator.FindAddOnFolder();
        }

        return folder is null ? null : AddOnLocator.DataFileFor(folder);
    }

    /// <summary>
    /// What a slugified realm or guild may contain before it is written into a Lua
    /// string literal. Deliberately narrower than the validator's header check: this
    /// is generated input, and there is no legitimate realm or guild slug with a
    /// quote, a backslash or a brace in it.
    /// </summary>
    [GeneratedRegex(@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z")]
    private static partial Regex SlugShape();

    /// <summary>Mirrors SidecarSettings.Key(), which is private.</summary>
    private static string DestinationKey(string destination)
    {
        try
        {
            return Path.GetFullPath(destination).ToLowerInvariant();
        }
        catch
        {
            return destination.ToLowerInvariant();
        }
    }
}
