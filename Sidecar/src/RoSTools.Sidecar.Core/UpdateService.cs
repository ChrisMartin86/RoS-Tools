namespace RoSTools.Sidecar.Core;

public enum UpdateOutcome
{
    /// <summary>A new roster was validated and installed.</summary>
    Updated,

    /// <summary>
    /// Server said 304, and the file on disk is still the one this sidecar put
    /// there. Nothing written.
    /// </summary>
    AlreadyCurrent,

    Failed,
}

public sealed record UpdateResult(
    UpdateOutcome Outcome,
    string Message,
    int Entries,
    string? GeneratedAt,
    DateTimeOffset AtUtc,
    long? GeneratedEpoch = null)
{
    public bool IsFailure => Outcome == UpdateOutcome.Failed;
}

/// <summary>
/// One check, end to end: resolve the addon folder, conditional GET, validate,
/// install, record what happened. Every failure path leaves the installed roster
/// exactly as it was.
/// </summary>
public sealed class UpdateService
{
    private readonly SettingsStore _store;
    private readonly GuildDataClient _client;

    public UpdateService(SettingsStore store, GuildDataClient client)
    {
        _store = store;
        _client = client;
    }

    public async Task<UpdateResult> CheckAsync(bool force, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Snapshot, don't alias. The Settings window can save while this check is
        // in flight; reading the live object would let a check install to the old
        // folder and then bank its ETag against the new one.
        var settings = _store.Snapshot();

        var addOnFolder = ResolveAddOnFolder(settings, out var resolveError);
        if (addOnFolder is null)
        {
            return Record(Fail(resolveError!, now));
        }

        var destination = AddOnLocator.DataFileFor(addOnFolder);
        var state = settings.StateFor(destination);

        // The cache may only be trusted when it describes *this* destination, from
        // *this* URL, and the file still sitting there is the one we installed.
        // Anything else - a CurseForge addon update, a half-written file, an
        // antivirus restore, a second addon folder - has to refetch, or the sidecar
        // reports "Already up to date" over data it did not put there.
        var installedStamp = GuildDataValidator.InstalledStamp(destination);
        var cacheUsable =
            state is not null &&
            string.Equals(state.Url, settings.DataUrl, StringComparison.Ordinal) &&
            state.Stamp is not null &&
            installedStamp == state.Stamp;

        if (!cacheUsable && !force && state is not null)
        {
            Log.Info(installedStamp is null
                ? "the installed roster is missing or unreadable; fetching unconditionally."
                : $"the installed roster ({installedStamp}) is not the one this sidecar wrote " +
                  $"({state.Stamp}); fetching unconditionally.");
        }

        var unconditional = force || !cacheUsable;

        var result = await _client
            .FetchAsync(settings.DataUrl, state?.ETag, state?.LastModified, unconditional, ct)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case FetchOutcome.Failed:
                return Record(Fail(result.Error ?? "the check failed.", now));

            case FetchOutcome.NotModified:
                // Only meaningful if we actually sent validators AND the file on disk
                // is still the one they describe. A 304 to an unconditional request
                // is a misbehaving proxy or mirror; treating it as "already current"
                // reported a healthy roster over a destination this method had
                // already decided it could not vouch for - and dereferenced a null
                // cache entry doing it, on a first install.
                if (!cacheUsable || state is null)
                {
                    return Record(Fail(
                        "The data source answered 304 to a request that carried no validators. " +
                        "Nothing was installed; check the URL for a caching proxy.",
                        now));
                }

                Log.Info("304 not modified; nothing written.");
                return Record(new UpdateResult(
                    UpdateOutcome.AlreadyCurrent,
                    "Already up to date.",
                    state.EntryCount,
                    state.GeneratedAt,
                    now,
                    state.Stamp));
        }

        var staging = result.StagingPath!;

        try
        {
            // Learned on the first install and enforced from then on. Falling back to
            // the installed file's own identity covers the upgrade case, where the
            // setting does not exist yet but a roster is already in place.
            var expected = settings.Guild ?? GuildDataValidator.IdentityOf(destination);

            var check = GuildDataValidator.Validate(staging, expected);
            if (!check.Ok)
            {
                Log.Warn($"refused the downloaded file: {check.Reason}");
                return Record(Fail(
                    $"Refused the new file: {check.Reason} Your existing roster is untouched.",
                    now));
            }

            // Never move the installed roster backwards in time.
            //
            // Core/Sync.lua orders the whole guild by generated_epoch, so installing
            // an older export is not a harmless no-op: it drops this client below the
            // data its own peers already adopted from it. Before the web console
            // existed this could not happen, because the branch file only ever moved
            // forwards. Now a hand-driven pull can install a roster newer than the
            // published one - which is precisely why someone would run one, CI having
            // failed - and the next poll would quietly put the stale branch file back
            // over it, reporting success.
            //
            // Deliberately strict '<': an equal epoch is the same export, and
            // reinstalling it is harmless.
            var installedNow = GuildDataValidator.InstalledStamp(destination);
            if (installedNow is { } currentEpoch &&
                check.GeneratedEpoch is { } incomingEpoch &&
                incomingEpoch < currentEpoch)
            {
                // No cache banked: these validators describe a file that was not
                // installed, and the cache's invariant is that an entry says what is
                // installed where. One unconditional fetch per interval is the cost.
                Log.Info(
                    $"kept the newer installed roster ({currentEpoch}); " +
                    $"the data source is offering an older one ({incomingEpoch}).");

                return Record(new UpdateResult(
                    UpdateOutcome.AlreadyCurrent,
                    "Kept the roster already installed -- it is newer than the published one.",
                    GuildDataValidator.Validate(destination).Entries,
                    GuildDataValidator.Validate(destination).GeneratedAt,
                    now,
                    currentEpoch));
            }

            // Re-check immediately before writing. The folder was validated before a
            // fetch that can take a minute, and an addon uninstall in that window
            // would otherwise have us recreate the folder we exist to serve.
            if (!AddOnLocator.LooksLikeAddOnFolder(addOnFolder))
            {
                return Record(Fail(
                    $"'{addOnFolder}' no longer contains {AddOnLocator.TocFileName} -- " +
                    "the addon was removed while the download was running.",
                    now));
            }

            DataInstaller.Install(staging, destination);
            staging = null!; // moved

            Log.Info($"installed {check.Entries} characters (exported {check.GeneratedAt}) to {destination}");

            _store.Update(s =>
            {
                // Only bank the cache if the settings still point where this check
                // installed. If the user re-pointed the sidecar mid-check, the entry
                // for this destination is still correct - it is keyed by path - but
                // a changed URL means these validators describe a different source.
                if (string.Equals(s.DataUrl, settings.DataUrl, StringComparison.Ordinal))
                {
                    var entry = s.StateForOrNew(destination);
                    entry.Url = settings.DataUrl;
                    entry.ETag = result.ETag;
                    entry.LastModified = result.LastModified;
                    entry.Stamp = check.GeneratedEpoch;
                    entry.EntryCount = check.Entries;
                    entry.GeneratedAt = check.GeneratedAt;
                }

                if (s.Guild is null && check.Identity is not null)
                {
                    s.GuildRegion = check.Identity.Region;
                    s.GuildRealm = check.Identity.Realm;
                    s.GuildName = check.Identity.Guild;
                    Log.Info($"this machine now carries {check.Identity}.");
                }

                s.LastUpdateUtc = now;
            });

            var message = $"Updated: {check.Entries} characters, exported {check.GeneratedAt}.";
            if (check.Warning is not null)
            {
                // Installed, but worth saying out loud: past Sync's MAX_AGE no
                // guildmate will accept this roster, so sharing has stopped and the
                // only thing keeping anyone current is this sidecar.
                Log.Warn(check.Warning);
                message += " " + check.Warning;
            }

            return Record(new UpdateResult(
                UpdateOutcome.Updated,
                message,
                check.Entries,
                check.GeneratedAt,
                now,
                check.GeneratedEpoch));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("install failed", ex);
            return Record(Fail($"Could not install the new file: {ex.Message}", now));
        }
        finally
        {
            if (staging is not null)
            {
                GuildDataClient.TryDelete(staging);
            }
        }
    }

    private static string? ResolveAddOnFolder(SidecarSettings settings, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(settings.AddOnPath))
        {
            if (AddOnLocator.LooksLikeAddOnFolder(settings.AddOnPath))
            {
                return settings.AddOnPath;
            }

            error = $"'{settings.AddOnPath}' no longer contains {AddOnLocator.TocFileName}. " +
                    "Point the sidecar at your RoS-Tools folder in Settings.";
            return null;
        }

        var detected = AddOnLocator.FindAddOnFolder();
        if (detected is not null)
        {
            return detected;
        }

        error = "Could not find an installed RoS-Tools addon. Set the addon folder in Settings.";
        return null;
    }

    private static UpdateResult Fail(string message, DateTimeOffset now) =>
        new(UpdateOutcome.Failed, message, 0, null, now);

    private UpdateResult Record(UpdateResult result)
    {
        _store.Update(s =>
        {
            s.LastCheckUtc = result.AtUtc;
            s.LastError = result.IsFailure ? result.Message : null;

            if (!result.IsFailure)
            {
                // Describe what is installed at the destination this check actually
                // used, so the tray never reports another folder's roster.
                s.LastEntryCount = result.Entries;
                s.LastGeneratedAt = result.GeneratedAt;
                s.LastGeneratedEpoch = result.GeneratedEpoch;
            }
        });

        return result;
    }
}
