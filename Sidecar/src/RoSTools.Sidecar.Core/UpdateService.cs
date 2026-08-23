namespace RoSTools.Sidecar.Core;

public enum UpdateOutcome
{
    /// <summary>A new roster was validated and installed.</summary>
    Updated,

    /// <summary>Server said 304, or the payload was byte-identical. Nothing written.</summary>
    AlreadyCurrent,

    Failed,
}

public sealed record UpdateResult(
    UpdateOutcome Outcome,
    string Message,
    int Entries,
    string? GeneratedAt,
    DateTimeOffset AtUtc)
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
        var settings = _store.Current;

        var addOnFolder = ResolveAddOnFolder(settings, out var resolveError);
        if (addOnFolder is null)
        {
            return Record(Fail(resolveError!, now));
        }

        var destination = AddOnLocator.DataFileFor(addOnFolder);

        // Auto-detect can only be trusted while the folder is still there; if the
        // remembered path has gone, say so rather than recreating it somewhere odd.
        var haveExisting = File.Exists(destination);

        var result = await _client
            .FetchAsync(settings.DataUrl, settings.ETag, settings.LastModified, force || !haveExisting, ct)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case FetchOutcome.Failed:
                return Record(Fail(result.Error ?? "the check failed.", now));

            case FetchOutcome.NotModified:
                Log.Info("304 not modified; nothing written.");
                return Record(new UpdateResult(
                    UpdateOutcome.AlreadyCurrent,
                    "Already up to date.",
                    settings.LastEntryCount,
                    settings.LastGeneratedAt,
                    now));
        }

        var staging = result.StagingPath!;

        try
        {
            var check = GuildDataValidator.Validate(staging);
            if (!check.Ok)
            {
                Log.Warn($"refused the downloaded file: {check.Reason}");
                return Record(Fail(
                    $"Refused the new file: {check.Reason}. Your existing roster is untouched.",
                    now));
            }

            DataInstaller.Install(staging, destination);
            staging = null!; // moved

            Log.Info($"installed {check.Entries} characters (exported {check.GeneratedAt}) to {destination}");

            _store.Update(s =>
            {
                s.ETag = result.ETag;
                s.LastModified = result.LastModified;
                s.LastUpdateUtc = now;
                s.LastEntryCount = check.Entries;
                s.LastGeneratedAt = check.GeneratedAt;
            });

            return Record(new UpdateResult(
                UpdateOutcome.Updated,
                $"Updated: {check.Entries} characters, exported {check.GeneratedAt}.",
                check.Entries,
                check.GeneratedAt,
                now));
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

    private string? ResolveAddOnFolder(SidecarSettings settings, out string? error)
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
        });

        return result;
    }
}
