using System.Collections.Concurrent;
using System.Text.Json;
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
    DateTimeOffset AtUtc,
    int Unreachable = 0,
    PullRefusal Refusal = PullRefusal.None)
{
    public static PullResult Failure(string error) =>
        new(false, error, null, null, 0, [], 0, 0, [], null, RosterDelta.Empty, DateTimeOffset.UtcNow);

    /// <summary>
    /// The answer to a caller that was never admitted.
    /// <para>
    /// It carries the refusal that <see cref="PullService.TryStart"/> actually made,
    /// because a null ticket means <i>either</i> "a pull is already running" <i>or</i>
    /// "not this soon" - and the convenience overload used to hard-code the first,
    /// so a throttled call was told a pull was running when none was.
    /// </para>
    /// </summary>
    public static PullResult Refused(PullStart start) =>
        new(false, start.Message ?? "The pull was refused.", null, null, 0, [], 0, 0, [], null,
            RosterDelta.Empty, DateTimeOffset.UtcNow, 0, start.Refusal);
}

public sealed record PullRequest(string Region, string Realm, string Guild, int MinLevel = 1);

/// <summary>
/// Proof that this caller, and only this caller, was admitted to run a pull.
/// <para>
/// Handed out by <see cref="PullService.TryStart"/> under the same lock that flips
/// the running flag, so the check and the flag cannot come apart. Everything that
/// used to sit between them on the request thread - a JSON deserialize, a settings
/// snapshot, a DPAPI <c>Unprotect</c> syscall - now happens after admission, where
/// a second request can no longer slip through.
/// </para>
/// </summary>
public sealed record PullTicket(Guid Id);

/// <summary>Why an admission was refused, or <see cref="None"/> when it was not.</summary>
public enum PullRefusal
{
    None,

    /// <summary>Another pull holds the slot.</summary>
    AlreadyRunning,

    /// <summary>The server-side minimum interval between pulls has not elapsed.</summary>
    TooSoon,
}

/// <summary>The outcome of <see cref="PullService.TryStart"/>.</summary>
public sealed record PullStart(PullTicket? Ticket, PullRefusal Refusal, string? Message)
{
    public bool Granted => Ticket is not null;
}

/// <summary>
/// A coherent view of the pull state, taken under the service's lock.
/// <para>
/// Reading <c>IsRunning</c>, <c>Progress</c> and <c>Last</c> separately let the
/// console see <c>running:false</c> beside a previous pull's successful result, and
/// render that stale roster with a live Install button and a green "ok".
/// </para>
/// </summary>
public sealed record PullStatus(bool Running, PullProgress Progress, PullResult? Last);

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

    /// <summary>
    /// The smallest gap the server will allow between two pulls that actually reach
    /// Blizzard.
    /// <para>
    /// The design's whole point is that CI stays the guild's single <i>scheduled</i>
    /// API consumer; the console's pull is the one deliberate exception, on a human
    /// click. Until now that property rested entirely on a disabled button and an
    /// in-memory flag - neither of which survives a page reload, a scripted POST or a
    /// restart of the app. Two minutes caps this machine at 30 pulls an hour, about
    /// 5,400 calls against a 36,000/hour limit, which is small enough that a runaway
    /// loop cannot spend the guild's quota and long enough to be obviously not a
    /// schedule. It is deliberately short because the stamp is only taken once a pull
    /// is past its pre-flight checks: a typo in the realm box costs nothing.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How far ahead of this machine's clock a stamp may sit before it is read as a
    /// clock that moved rather than as a pull that happened.
    /// <para>
    /// A stamp is written from <see cref="DateTimeOffset.UtcNow"/> on this machine and
    /// read back on the same machine, so the only thing that puts one in the future is
    /// the clock itself: a dead RTC at boot, a VM restored from a skewed snapshot, an
    /// NTP correction between the write and the read. Five minutes is far more than
    /// any of that costs legitimately, and honouring anything beyond it is what locked
    /// a machine out for the length of the jump.
    /// </para>
    /// </summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

    private readonly SettingsStore _store;
    private readonly Func<string, BlizzardApiClient> _clientFactory;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly TimeSpan _minimumInterval;

    /// <summary>
    /// Guards admission, cancellation and the reported state together.
    /// <para>
    /// The check and the flag used to be two statements on the request thread with a
    /// JSON deserialize, a settings snapshot and a DPAPI syscall between them. A
    /// second POST landing in that window was admitted, and an <c>/api/install</c>
    /// landing in it took the semaphore first, so the queued pull returned without
    /// touching <c>Last</c> or <c>Progress</c> and the console rendered the PREVIOUS
    /// pull as a fresh success.
    /// </para>
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>The admitted pull, or <see cref="Guid.Empty"/> when the slot is free.</summary>
    private Guid _active;

    /// <summary>
    /// Only ever set by the pull that owns <see cref="_active"/>, and cleared by that
    /// same pull. Keying it to the ticket is what stops a losing request's source
    /// from overwriting the winner's and turning <c>DELETE /api/pull</c> into a
    /// no-op that still answers <c>{"ok":true,"message":"Cancelling."}</c>.
    /// </summary>
    private CancellationTokenSource? _activeCancellation;

    /// <summary>
    /// A cancel that arrived after admission but before the pull registered its
    /// source. The console shows the Cancel button the moment the POST returns, so
    /// that window is reachable by an ordinary click - and answering "nothing to
    /// cancel" there would be the same lie the old code told, just narrower.
    /// </summary>
    private bool _cancelPending;

    private DateTimeOffset? _lastPullUtc;
    private bool _stampLoaded;

    /// <summary>
    /// Written and read only under <see cref="_gate"/>.
    /// <para>
    /// It used to be an auto-property assigned bare from <c>RunAsync</c> and from
    /// eight parallel character workers while <c>Status()</c>, <c>TryStart()</c>,
    /// <c>Abandon()</c> and <c>Publish()</c> all touched it under the lock. Nothing
    /// tore - it is a reference - but the pair <c>Status()</c> ships was not
    /// guaranteed to describe one moment, which is the entire reason
    /// <see cref="PullStatus"/> exists.
    /// </para>
    /// </summary>
    private PullProgress _progress = PullProgress.Idle;

    public PullService(
        SettingsStore store,
        Func<string, BlizzardApiClient>? clientFactory = null,
        TimeSpan? minimumInterval = null)
    {
        _store = store;
        _clientFactory = clientFactory ?? (region => new BlizzardApiClient(region));
        _minimumInterval = minimumInterval ?? DefaultMinimumInterval;

        // Resolved once, to an absolute path, at construction. See StampPath.
        StampPath = ResolveStampPath(store.Path);
    }

    public PullProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    /// <summary>The last completed pull, or null. Staged but not installed.</summary>
    public PullResult? Last { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _active != Guid.Empty;
            }
        }
    }

    /// <summary>The minimum gap this instance enforces between pulls.</summary>
    public TimeSpan MinimumInterval => _minimumInterval;

    /// <summary>
    /// Where the "when did this machine last spend Blizzard quota" stamp lives:
    /// beside <c>sidecar.json</c>, in its own file.
    /// <para>
    /// Its own file rather than a settings field because the throttle has to survive
    /// a settings file the store decides is corrupt and replaces with defaults - that
    /// is exactly the state a restart loop would leave behind.
    /// </para>
    /// <para>
    /// <b>Absolute, and resolved once.</b> <c>Paths.StateDirectory</c> is built from
    /// <c>Environment.GetFolderPath(LocalApplicationData)</c>, which returns an empty
    /// string when it cannot be resolved - and the settings path, and therefore this
    /// one, is then relative to the process's current directory. A throttle whose file
    /// moves when something calls <c>SetCurrentDirectory</c> is no throttle at all: the
    /// next read finds no stamp and opens the gate. Pinning it at construction makes
    /// the file this instance writes the file this instance reads, whatever the working
    /// directory does afterwards.
    /// </para>
    /// </summary>
    public string StampPath { get; }

    private static string ResolveStampPath(string settingsPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);

            return Path.GetFullPath(string.IsNullOrEmpty(directory)
                ? "pull-state.json"
                : Path.Combine(directory, "pull-state.json"));
        }
        catch (Exception ex)
        {
            // A settings path this process cannot even turn into a full path. The
            // throttle still needs somewhere to live, and somewhere writable beats
            // nowhere: losing it entirely is the failure mode that costs the quota.
            var fallback = Path.Combine(Path.GetTempPath(), "rostools-pull-state.json");
            Log.Warn($"could not place the pull throttle stamp beside the settings file " +
                     $"({ex.Message}); using {fallback}.");

            return fallback;
        }
    }

    /// <summary>
    /// Atomically decides whether a pull may start and claims the slot if so.
    /// <para>
    /// The returned ticket is the caller's proof of admission; pass it to
    /// <see cref="PullAsync(BlizzardCredentials, PullRequest, PullTicket, CancellationToken)"/>.
    /// A refusal names which rule refused and, for the throttle, when the next pull
    /// is allowed.
    /// </para>
    /// </summary>
    public PullStart TryStart()
    {
        lock (_gate)
        {
            if (_active != Guid.Empty)
            {
                return new PullStart(null, PullRefusal.AlreadyRunning, "A pull is already running.");
            }

            if (_minimumInterval > TimeSpan.Zero && LastPullLocked() is { } last)
            {
                var next = last + _minimumInterval;
                var now = DateTimeOffset.UtcNow;

                if (next > now)
                {
                    var wait = next - now;
                    return new PullStart(
                        null,
                        PullRefusal.TooSoon,
                        $"The last pull ran at {last.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC. " +
                        $"Pulls are limited to one every {Describe(_minimumInterval)} so this machine " +
                        "never becomes a second scheduled consumer of the guild's API quota. " +
                        $"The next one is allowed at {next.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC, " +
                        $"in {Describe(wait)}. " +
                        // Named, because a throttle with no way out is worse than no
                        // throttle: the clamp above bounds a bad clock to one interval,
                        // and this is the manual answer if it is ever wrong anyway.
                        $"The stamp is {StampPath}; deleting that file clears the throttle.");
                }
            }

            var id = Guid.NewGuid();
            _active = id;
            _cancelPending = false;
            _progress = new PullProgress(PullPhase.Authenticating, 0, 0, "Starting...");
            return new PullStart(new PullTicket(id), PullRefusal.None, null);
        }
    }

    /// <summary>
    /// Gives an admitted slot back without running anything.
    /// <para>
    /// For a caller that took a ticket and then decided not to pull - a missing realm,
    /// no credentials, a throw on the way to queueing the task. Without it the slot
    /// stays claimed for the life of the process and the console reports a pull that
    /// does not exist.
    /// </para>
    /// </summary>
    public void Abandon(PullTicket ticket)
    {
        lock (_gate)
        {
            if (_active == ticket.Id)
            {
                _active = Guid.Empty;
                _activeCancellation = null;
                _cancelPending = false;

                // TryStart moved Progress to "Starting..."; put it back to whatever
                // the last real pull left behind, so the page does not sit on a
                // phase for a pull that never began.
                _progress = Last is null ? PullProgress.Idle : ProgressFor(Last);
            }
        }
    }

    /// <summary>
    /// Cancels the admitted pull, if there is one. Returns false when there was
    /// nothing to cancel - the console must not answer "Cancelling." over a pull that
    /// is going to run to completion and spend the whole quota.
    /// </summary>
    public bool CancelActive()
    {
        lock (_gate)
        {
            if (_active == Guid.Empty)
            {
                return false;
            }

            if (_activeCancellation is null)
            {
                // Admitted, but not yet holding a source. It will honour this the
                // instant it registers one.
                _cancelPending = true;
                return true;
            }

            try
            {
                _activeCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The pull finished between the read and the call. Nothing to cancel.
                return false;
            }

            return true;
        }
    }

    /// <summary>Running flag, progress and last result read together, under the lock.</summary>
    public PullStatus Status()
    {
        lock (_gate)
        {
            return new PullStatus(_active != Guid.Empty, _progress, Last);
        }
    }

    /// <summary>
    /// Admits and runs in one call. Convenience for callers that are not racing
    /// anything - the tests, and anything that owns the service outright.
    /// </summary>
    public Task<PullResult> PullAsync(
        BlizzardCredentials credentials,
        PullRequest request,
        CancellationToken ct = default) =>
        PullAsync(credentials, request, TryStart(), ct);

    /// <summary>
    /// Runs an admission taken by <see cref="TryStart"/>, refusal and all.
    /// <para>
    /// It takes the whole <see cref="PullStart"/> rather than its ticket because the
    /// ticket alone cannot say <i>why</i> a caller was not admitted:
    /// <see cref="PullRefusal.AlreadyRunning"/> and <see cref="PullRefusal.TooSoon"/>
    /// both produce a null one. The convenience overload passed only the ticket, so a
    /// throttled call came back "A pull is already running." when no pull was.
    /// </para>
    /// </summary>
    public Task<PullResult> PullAsync(
        BlizzardCredentials credentials,
        PullRequest request,
        PullStart start,
        CancellationToken ct = default) =>
        start.Ticket is { } ticket
            ? PullAsync(credentials, request, ticket, ct)

            // A refusal never held the slot, so it never owned the reported state
            // either. Publishing it - which is what this used to do - replaced a Last
            // describing a pull that genuinely succeeded with the refusal, dropped the
            // console's Install button for a roster still sitting on disk, and left
            // that roster in %TEMP% forever because disposePrevious was false. The
            // refusal is this caller's answer and nobody else's.
            : Task.FromResult(PullResult.Refused(start));

    public async Task<PullResult> PullAsync(
        BlizzardCredentials credentials,
        PullRequest request,
        PullTicket ticket,
        CancellationToken ct = default)
    {
        // The cancellation source is created and registered here, by the pull that
        // was actually admitted, and keyed to its ticket. It used to be created and
        // stored by the request thread BEFORE admission, so a losing request
        // overwrote the winner's source and then nulled the field in its own finally
        // - after which DELETE /api/pull answered "Cancelling." while the winner ran
        // its ~180 calls to completion.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var cancelAtOnce = false;

        lock (_gate)
        {
            if (_active == ticket.Id)
            {
                _activeCancellation = cancellation;
                cancelAtOnce = _cancelPending;
                _cancelPending = false;
            }
        }

        if (cancelAtOnce)
        {
            // Someone hit Cancel while this pull was still resolving credentials.
            cancellation.Cancel();
        }

        // Nothing may escape this method. It is started with Task.Run and never
        // awaited, so an exception here is an unobserved fault: Last keeps the
        // PREVIOUS pull, Progress freezes mid-count, IsRunning goes false, and the
        // page then renders that stale roster with a live Install button and no
        // error anywhere. The semaphore acquisition below is itself a throw site -
        // WaitAsync raises OperationCanceledException on an already-cancelled token,
        // outside every catch further in - so the guard has to start here.
        try
        {
            return await PullCoreAsync(credentials, request, cancellation.Token).ConfigureAwait(false);
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

            Publish(failure, disposePrevious: true);
            return failure;
        }
        finally
        {
            // Released under the same lock that admitted it, and only by the pull
            // that holds the ticket, so a late finisher can never free a slot a newer
            // pull has taken.
            lock (_gate)
            {
                if (_active == ticket.Id)
                {
                    _active = Guid.Empty;
                    _activeCancellation = null;
                    _cancelPending = false;
                }
            }
        }
    }

    private async Task<PullResult> PullCoreAsync(
        BlizzardCredentials credentials,
        PullRequest request,
        CancellationToken ct)
    {
        // Only an install can hold this now - admission already made a second pull
        // impossible. An install is a local file copy, so waiting is right where
        // failing was not: the old code gave up immediately and returned WITHOUT
        // assigning Last or Progress, leaving the previous pull's success on screen.
        var held = false;
        try
        {
            held = await _oneAtATime.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

            var result = held
                ? await RunAsync(credentials, request, ct).ConfigureAwait(false)
                : PullResult.Failure(
                    "An install is still running after 30 seconds, so the pull did not start. " +
                    "Try again in a moment -- nothing was fetched.");

            Publish(result, disposePrevious: true);
            return result;
        }
        finally
        {
            if (held)
            {
                _oneAtATime.Release();
            }
        }
    }

    /// <summary>
    /// Makes a result the reported state. Both fields move together under the lock,
    /// so <c>/api/pull</c> can never pair a finished flag with a previous run's
    /// result.
    /// </summary>
    private void Publish(PullResult result, bool disposePrevious)
    {
        string? previous = null;

        lock (_gate)
        {
            // Replacing a staged-but-uninstalled pull deletes its file; otherwise
            // every discarded attempt leaves a roster in %TEMP% forever.
            if (disposePrevious && !ReferenceEquals(Last, result))
            {
                previous = Last?.StagingPath;
            }

            Last = result;
            _progress = ProgressFor(result);
        }

        if (previous is not null)
        {
            GuildDataClient.TryDelete(previous);
        }
    }

    /// <summary>
    /// The only way a running pull moves the reported phase. Under the lock, so the
    /// pair <see cref="Status"/> returns describes one moment rather than two.
    /// </summary>
    private void SetProgress(PullProgress progress)
    {
        lock (_gate)
        {
            _progress = progress;
        }
    }

    /// <summary>
    /// Publishes a character count, dropping one a faster worker has already passed.
    /// <para>
    /// Eight workers reach here: each takes its number from an <c>Interlocked</c>
    /// increment and then publishes it, and nothing orders those two steps against
    /// each other. Unsynchronised, a worker overtaken in that window published
    /// "45 / 180" after another had published "50 / 180" and the console's bar walked
    /// backwards - progress describing a moment that had already passed. Keeping the
    /// furthest count this run has reached is the only count it can honestly report.
    /// </para>
    /// <para>Internal so the drop can be tested without racing eight threads.</para>
    /// </summary>
    internal void ReportCharacterProgress(int completed, int total)
    {
        lock (_gate)
        {
            if (_progress.Phase == PullPhase.FetchingCharacters && _progress.Done >= completed)
            {
                return;
            }

            _progress = new PullProgress(
                PullPhase.FetchingCharacters, completed, total, $"{completed} / {total} characters...");
        }
    }

    private static PullProgress ProgressFor(PullResult result) =>
        result.Ok
            ? new PullProgress(PullPhase.Done, result.Entries.Count, result.RosterSize,
                $"Pulled {result.Entries.Count} characters.")
            : new PullProgress(PullPhase.Failed, 0, 0, result.Error ?? "The pull failed.");

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
                    $"'{value}' is not a usable {field} name: it contains one of " +
                    "\" \\ { } : ; = | or a control character, which the generated Lua file and " +
                    "the roster-sharing header cannot carry. Use the plain name as it appears " +
                    "in-game.");
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
            // Stamped here, not at admission: everything above this line is a
            // pre-flight check that costs no quota, so a typo in the realm box must
            // not lock the console out for the next two minutes. Everything below it
            // does spend quota, whether or not it ends up succeeding.
            StampPull();

            SetProgress(new PullProgress(PullPhase.Authenticating, 0, 0, "Signing in to Blizzard..."));
            await client.AuthenticateAsync(credentials.ClientId, credentials.ClientSecret, ct)
                .ConfigureAwait(false);

            SetProgress(new PullProgress(PullPhase.FetchingRoster, 0, 0, $"Fetching the {guildSlug} roster..."));
            var roster = await client.GetRosterAsync(realmSlug, guildSlug, ct).ConfigureAwait(false);

            var targets = roster.Where(m => m.Level >= request.MinLevel).ToList();
            if (targets.Count == 0)
            {
                return PullResult.Failure(
                    $"The roster came back with no characters at or above level {request.MinLevel}.");
            }

            SetProgress(new PullProgress(
                PullPhase.FetchingCharacters, 0, targets.Count,
                $"0 / {targets.Count} characters..."));

            var entries = new ConcurrentBag<RosterEntry>();
            var done = 0;
            var missing = 0;
            var unreachable = 0;

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
                        // count is reported, and the guards in Install() are what stop
                        // a pull that lost too many from reaching the guild.
                        //
                        // Counted SEPARATELY from a 404: a character with no profile is
                        // an ordinary, permanent fact about a guild full of alts, while
                        // this is Blizzard failing after five attempts with backoff.
                        // Only the second kind says "come back later", and only the
                        // second kind may block an install on its own.
                        Interlocked.Increment(ref unreachable);
                        Log.Warn($"could not read {member.Key}: {ex.Message}");
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // HttpClient's own timeout. Same treatment as any other failed
                        // character: counted as missing, not fatal to the whole pull.
                        Interlocked.Increment(ref unreachable);
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
                        ReportCharacterProgress(completed, targets.Count);
                    }
                }).ConfigureAwait(false);

            var collected = entries.ToList();
            if (collected.Count == 0)
            {
                return PullResult.Failure(
                    "Every character came back without an item level. That usually means the " +
                    "profile namespace is not enabled for this client, or nobody has logged in.");
            }

            SetProgress(new PullProgress(
                PullPhase.Writing, collected.Count, targets.Count, "Writing the roster..."));

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
                $"({missing} without a profile, {unreachable} unreachable, " +
                $"{dropped.Count} keys dropped)");

            return new PullResult(
                true, null, staging, identity, epoch,
                written.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList(),
                targets.Count, missing, dropped, check, delta, DateTimeOffset.UtcNow, unreachable);
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

    /// <summary>
    /// How much of the roster a single pull may fail to READ before
    /// <see cref="InstallAsync"/> refuses it without an explicit override.
    /// <para>
    /// <see cref="ShrinkFloor"/> is relative and needs a baseline, and it is skipped
    /// outright when there isn't one - a fresh machine, or one whose installed roster
    /// the validator rejects, which
    /// <c>PullGuardTests.A_corrupt_installed_roster_is_not_a_shrink_baseline</c>
    /// deliberately blesses. On those machines the only remaining check was
    /// <c>collected.Count &gt; 0</c>: Blizzard has a bad ten minutes, 177 of 180
    /// characters come back 503 after five attempts each, and a three-entry roster
    /// validates, installs, takes a fresh <c>generated_epoch</c> and is announced to
    /// the guild by <c>Core/Sync.lua</c> as the newest data anyone has.
    /// </para>
    /// <para>
    /// 10% is the line, and it needs no baseline file because <c>RunAsync</c> counts
    /// the misses itself. A character that answers 404 is NOT a miss - guilds are full
    /// of alts that have never logged in, and a permanent fact about the roster must
    /// not read as an outage. A miss is Blizzard failing after five attempts with
    /// backoff, which is rare enough that more than one in ten means the API is having
    /// a bad time and this pull is not a picture of the guild.
    /// </para>
    /// </summary>
    public const double MaxUnreachableFraction = 0.10;

    public sealed record InstallOutcome(bool Ok, string Message, int Entries, bool NeedsOverride = false);

    /// <summary>
    /// Installs a staged pull. Separate from <see cref="PullAsync(BlizzardCredentials, PullRequest, CancellationToken)"/>
    /// on purpose: the console shows the data first and this only runs on a second,
    /// explicit click.
    /// <para>
    /// Async because it is awaited from an HTTP handler. The blocking
    /// <c>SemaphoreSlim.Wait(30s)</c> this replaced parked a thread-pool thread for up
    /// to half a minute inside <c>ConsoleServer</c>'s request path, which is where
    /// <c>/api/pull</c> progress polling also lands.
    /// </para>
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(bool overrideShrink)
    {
        // Serialized against PullAsync and against another Install. Without this, a
        // pull completing mid-install deletes the staging file between the shrink
        // check and the copy, and two concurrent installs both clear the same guard.
        if (!await _oneAtATime.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
        {
            return new InstallOutcome(false, "A pull is running. Try again when it finishes.", 0);
        }

        try
        {
            // The semaphore alone is not the guard any more. A pull that has been
            // admitted but has not yet reached the semaphore - it is still resolving
            // credentials, which on Windows means a DPAPI syscall - would let this
            // install run against a `Last` that pull is about to replace, deleting the
            // staging file out from under the copy. Refusing is the honest answer: the
            // console has a pull in flight.
            if (IsRunning)
            {
                return new InstallOutcome(false, "A pull is running. Try again when it finishes.", 0);
            }

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

        // Absolute, and checked BEFORE the relative guard below, because this is the
        // one that still works on a machine with no usable baseline - which is
        // precisely the machine the relative guard silently skips. See
        // MaxUnreachableFraction.
        if (!overrideShrink && result.RosterSize > 0 &&
            result.Unreachable > result.RosterSize * MaxUnreachableFraction)
        {
            var percent = (int)Math.Round(result.Unreachable * 100.0 / result.RosterSize);

            return new InstallOutcome(
                false,
                $"Blizzard could not be read for {result.Unreachable} of {result.RosterSize} " +
                $"characters in this pull -- {percent}%, against a {(int)(MaxUnreachableFraction * 100)}% " +
                $"limit. What is staged is {check.Entries} characters, not the guild. Installing it " +
                "would stamp a fresh export time and announce that short roster to everyone running " +
                "RoS-Tools. Pull again in a few minutes, or tick the override if you are certain.",
                check.Entries,
                NeedsOverride: true);
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
    /// What a slugified realm or guild may contain: anything that is not one of the
    /// handful of characters that break something downstream.
    /// <para>
    /// Defined by exclusion, and that is the whole point. The old pattern was
    /// <c>[a-z0-9]+(-[a-z0-9]+)*</c>, which is ASCII-only - but
    /// <see cref="BlizzardApiClient.Slugify"/> deliberately does not fold diacritics
    /// (<c>Éonar</c> slugs to <c>éonar</c>, pinned by <c>BlizzardApiClientTests</c>
    /// and matching <c>Tools/fetch_guild_info.py</c>), so a guild called "Légion" died
    /// here before a single API call, and was told the name it had typed correctly was
    /// not a usable guild name. It was also strictly narrower than the guild-wide
    /// admission gate it claimed to pre-empt:
    /// <c>GuildDataValidator.HeaderUnsafe</c> rejects only <c>[:;|]</c>.
    /// </para>
    /// <para>
    /// So this rejects exactly what breaks something downstream and nothing else:
    /// <c>"</c> and <c>\</c> break the generated Lua string literal (and the
    /// validator's own <c>[^"\r\n]*</c> meta capture); <c>{</c> and <c>}</c> break the
    /// brace scan the skeleton check runs; <c>:</c>, <c>;</c> and <c>|</c> are the
    /// <c>Core/Sync.lua</c> <c>H:</c> header's field separators and chat markup;
    /// <c>=</c> is the assignment the meta parser splits on; and control characters,
    /// newlines included, end the literal outright.
    /// </para>
    /// <para>
    /// Anchored at both ends over one negated character class under one quantifier.
    /// There is no alternation and no nested quantifier, so it cannot backtrack over
    /// whatever a realm box is filled with, and the <c>+</c> also rejects an empty
    /// slug.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"\A[^""\\{}:;=|\p{Cc}\p{Cf}\p{Zl}\p{Zp}]+\z")]
    private static partial Regex SlugShape();

    // ------------------------------------------------------------------
    // Throttle state
    // ------------------------------------------------------------------

    /// <summary>The on-disk shape of <see cref="StampPath"/>. One field, on purpose.</summary>
    private sealed record PullStamp(DateTimeOffset LastPullUtc);

    /// <summary>
    /// The last pull this machine recorded, or null when it has never recorded one.
    /// <para>
    /// <b>One rule for every unusable stamp:</b> a file that is there but cannot be
    /// believed - unreadable, not JSON, empty, or dated further into the future than
    /// <see cref="ClockSkewTolerance"/> allows - is read as <i>a pull that just
    /// happened</i>, and rewritten to say so. That is deliberately not the same as no
    /// file at all, which is a first run and opens the gate.
    /// </para>
    /// <para>
    /// It settles the two failure modes together. Failing <i>open</i> on a corrupt
    /// stamp is what costs the guild's quota: a crash loop that leaves a half-written
    /// file behind gets an unthrottled pull on every restart, which is precisely the
    /// scheduled second consumer this whole mechanism exists to prevent. Failing
    /// <i>closed</i> on a stamp taken at face value is what locked a machine out for a
    /// year: one pull under a dead RTC, an NTP correction, and no path back through
    /// settings, the console or a restart. Clamping to now costs at most one interval -
    /// two minutes by default - in either case, and the rewrite means the next process
    /// reads an ordinary stamp rather than failing closed all over again.
    /// </para>
    /// <para>Call with <see cref="_gate"/> held.</para>
    /// </summary>
    private DateTimeOffset? LastPullLocked()
    {
        if (_stampLoaded)
        {
            return _lastPullUtc;
        }

        _stampLoaded = true;

        var now = DateTimeOffset.UtcNow;
        var clamped = false;

        try
        {
            if (File.Exists(StampPath))
            {
                var stamp = JsonSerializer.Deserialize<PullStamp>(File.ReadAllText(StampPath));

                if (stamp is null || stamp.LastPullUtc <= DateTimeOffset.UnixEpoch)
                {
                    // Present, parsed, and still not a stamp: "null", "{}", a file
                    // truncated to nothing. Indistinguishable from a first run in
                    // content, but not in fact - something wrote this.
                    Log.Warn(
                        $"the pull throttle stamp at {StampPath} does not carry a usable time. " +
                        "Treating it as a pull that just ran and rewriting it.");
                    clamped = true;
                }
                else if (stamp.LastPullUtc > now + ClockSkewTolerance)
                {
                    Log.Warn(
                        $"the pull throttle stamp at {StampPath} is dated " +
                        $"{stamp.LastPullUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC, which is ahead of " +
                        "this machine's clock. That is a clock that moved, not a pull; treating it " +
                        "as a pull that just ran and rewriting it.");
                    clamped = true;
                }
                else
                {
                    _lastPullUtc = stamp.LastPullUtc;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"could not read the pull throttle stamp at {StampPath}: {ex.Message}. " +
                $"Treating it as a pull that just ran; the next one is allowed in " +
                $"{Describe(_minimumInterval)}.");
            clamped = true;
        }

        if (clamped)
        {
            _lastPullUtc = now;
            WriteStamp(now);
        }

        return _lastPullUtc;
    }

    /// <summary>
    /// Records that this machine is about to spend Blizzard quota. Persisted, so a
    /// restart - or a crash loop - does not reset the interval.
    /// </summary>
    private void StampPull()
    {
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            _lastPullUtc = now;
            _stampLoaded = true;
        }

        WriteStamp(now);
    }

    private void WriteStamp(DateTimeOffset at)
    {
        try
        {
            var directory = Path.GetDirectoryName(StampPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-rename, so a crash cannot leave half a stamp file - which
            // would read back as a stamp that cannot be believed, and cost the next
            // caller an interval.
            var staging = StampPath + ".tmp";
            File.WriteAllText(staging, JsonSerializer.Serialize(new PullStamp(at)));
            File.Move(staging, StampPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not record the pull throttle stamp at {StampPath}: {ex.Message}");
        }
    }

    /// <summary>A duration a person reading a refusal message can act on.</summary>
    private static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalMinutes < 1)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        var minutes = (int)Math.Ceiling(span.TotalMinutes);
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

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
