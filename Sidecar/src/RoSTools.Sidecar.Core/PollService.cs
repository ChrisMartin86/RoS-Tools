namespace RoSTools.Sidecar.Core;

/// <summary>
/// The loop's view of time. Production uses <see cref="Task.Delay(TimeSpan, CancellationToken)"/>;
/// the tests substitute a controllable one so the schedule can be asserted without
/// waiting six hours for it. Injecting this is the only reason the constructor grew
/// a third parameter - it is optional and the production call sites are unchanged.
/// </summary>
public interface IPollClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken token);
}

internal sealed class SystemPollClock : IPollClock
{
    public static readonly SystemPollClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}

/// <summary>
/// The background loop. Checks shortly after startup, then on the configured
/// interval with jitter so a whole guild does not hit the data source on the
/// same minute. Transport failures back off exponentially; a success resets it.
/// </summary>
public sealed class PollService : IAsyncDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for work already running before it
    /// stops waiting. Matches <c>TrayContext.PullQuiesceTimeout</c> and
    /// <c>ConsoleServer</c>'s own cap, because it is the same two seconds of the same
    /// user's shutdown: <c>TrayContext.Dispose</c> blocks the UI thread on this.
    /// </summary>
    private static readonly TimeSpan QuiesceTimeout = TimeSpan.FromSeconds(2);

    private readonly SettingsStore _store;
    private readonly UpdateService _updates;
    private readonly IPollClock _clock;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Random _jitter = new();

    /// <summary>
    /// Guards <see cref="_loop"/>, <see cref="_disposed"/> and the in-flight check
    /// count. Never held across an await.
    /// </summary>
    private readonly Lock _gate = new();

    private Task? _loop;
    private TimeSpan _backoff = TimeSpan.Zero;
    private CancellationTokenSource? _sleep;
    private bool _disposed;
    private int _checksInFlight;
    private TaskCompletionSource? _drained;

    public PollService(SettingsStore store, UpdateService updates, IPollClock? clock = null)
    {
        _store = store;
        _updates = updates;
        _clock = clock ?? SystemPollClock.Instance;
    }

    public event Action? CheckStarted;

    public event Action<UpdateResult>? CheckCompleted;

    /// <summary>
    /// When the loop will next check, or null when there is no such moment: a check
    /// is running right now, the loop has not been started, or it has stopped. It is
    /// never a time in the past - the tray renders this directly, and a stale stamp
    /// read as an overdue check that was never coming.
    /// </summary>
    public DateTimeOffset? NextCheckUtc { get; private set; }

    /// <summary>
    /// Idempotent, including under concurrent callers. <c>_loop ??= Task.Run(...)</c>
    /// is a read, a branch and a write with nothing between them, so two threads
    /// could each start a loop and only one of them would ever be awaited on dispose.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _loop is not null)
            {
                return;
            }

            _loop = Task.Run(RunAsync);
        }
    }

    /// <summary>
    /// Runs a check now and reschedules the loop from this moment. Safe to call
    /// while the loop is mid-check - the second caller waits rather than racing -
    /// and safe to call concurrently with <see cref="DisposeAsync"/>, which waits
    /// for anything already in flight rather than disposing underneath it.
    /// </summary>
    public async Task<UpdateResult> CheckNowAsync(bool force = false)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                // The UI fires this and forgets it, and the console server holds it
                // as a delegate, so a click landing during shutdown is ordinary. Say
                // so rather than throwing ObjectDisposedException at nobody.
                return ShuttingDown();
            }

            _checksInFlight++;
        }

        try
        {
            var result = await ExecuteAsync(force, _shutdown.Token).ConfigureAwait(false);
            WakeLoop();
            return result;
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return ShuttingDown();
        }
        finally
        {
            lock (_gate)
            {
                if (--_checksInFlight == 0)
                {
                    _drained?.TrySetResult();
                }
            }
        }
    }

    private bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    private UpdateResult ShuttingDown() => new(
        UpdateOutcome.Failed,
        "The sidecar is shutting down; the check did not run.",
        0,
        null,
        _clock.UtcNow);

    private async Task RunAsync()
    {
        var token = _shutdown.Token;

        try
        {
            await _clock.DelayAsync(StartupDelay, token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                var result = await ExecuteAsync(force: false, token).ConfigureAwait(false);

                _backoff = result.IsFailure
                    ? (_backoff == TimeSpan.Zero
                        ? MinimumBackoff
                        : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaximumBackoff.Ticks)))
                    : TimeSpan.Zero;

                // A wake is CheckNowAsync telling us it has *already* run a check and
                // wants the schedule moved to now - which is what its documentation
                // promises. Falling out of the sleep into the loop body instead ran a
                // second check immediately: two HTTP requests per click, and a tray
                // that said "Updated: 187 characters" and then overwrote it with
                // "Already up to date." a moment later.
                while (await SleepAsync(NextDelay(), token).ConfigureAwait(false) == SleepOutcome.Woken)
                {
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutting down. Guarded on the token: a cancellation from anywhere else
            // - the linked sleep source, most plausibly - is a bug in this class, not
            // a shutdown, and must not be mistaken for one.
        }
        catch (Exception ex)
        {
            // The loop is the only thing keeping the roster current, so its death has
            // to reach the tray. Logging alone left a dead sidecar rendering a healthy
            // icon and an ever-older "updated N days ago" for the life of the process.
            Log.Error("the poll loop stopped unexpectedly", ex);

            var message = $"The sidecar stopped checking for updates: {ex.Message} " +
                          "Restart it to resume.";

            _store.Update(s => s.LastError = message);

            CheckCompleted?.Invoke(new UpdateResult(
                UpdateOutcome.Failed, message, 0, null, _clock.UtcNow));
        }
        finally
        {
            // However the loop ended, nothing is scheduled any more. Leaving the last
            // stamp behind left the tray promising a check that was never coming.
            NextCheckUtc = null;
        }
    }

    private TimeSpan NextDelay()
    {
        var baseline = TimeSpan.FromHours(_store.Current.EffectivePollHours);

        if (_backoff > TimeSpan.Zero)
        {
            // The *longer* of the two, never the shorter. A failure includes a
            // *content* refusal, not just a transport one, so taking the minimum here
            // moved a 6-hourly client to a five-minute poll settling at hourly the
            // moment a bad export went up - four times the traffic, with the jitter
            // window shrunk to match, so every failing client in the guild converged
            // on the same few minutes. Backing off must never poll more often than
            // success does.
            baseline = _backoff > baseline ? _backoff : baseline;
        }

        // +/-10%, so installs spread out instead of stampeding on the hour.
        var spread = 1.0 + ((_jitter.NextDouble() - 0.5) / 5.0);
        return TimeSpan.FromTicks((long)(baseline.Ticks * spread));
    }

    private enum SleepOutcome
    {
        /// <summary>The delay ran out; it is time for the next scheduled check.</summary>
        Elapsed,

        /// <summary>CheckNowAsync ran a check and wants the schedule moved to now.</summary>
        Woken,
    }

    private async Task<SleepOutcome> SleepAsync(TimeSpan delay, CancellationToken token)
    {
        NextCheckUtc = _clock.UtcNow + delay;

        using var sleep = CancellationTokenSource.CreateLinkedTokenSource(token);
        _sleep = sleep;

        try
        {
            await _clock.DelayAsync(delay, sleep.Token).ConfigureAwait(false);
            return SleepOutcome.Elapsed;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return SleepOutcome.Woken;
        }
        finally
        {
            _sleep = null;
        }
    }

    private void WakeLoop()
    {
        try
        {
            _sleep?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The loop already moved on.
        }
    }

    private async Task<UpdateResult> ExecuteAsync(bool force, CancellationToken token)
    {
        await _oneAtATime.WaitAsync(token).ConfigureAwait(false);

        // There is no next check while one is running, and the stamp SleepAsync left
        // behind is now in the past. The tray reads this straight out.
        NextCheckUtc = null;

        try
        {
            CheckStarted?.Invoke();
            var result = await _updates.CheckAsync(force, token).ConfigureAwait(false);
            CheckCompleted?.Invoke(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("check threw", ex);

            var result = new UpdateResult(
                UpdateOutcome.Failed,
                $"The check failed: {ex.Message}",
                0,
                null,
                _clock.UtcNow);

            CheckCompleted?.Invoke(result);
            return result;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        Task? drained = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;

            // Anything already past the disposed check owns a slot in this count, so
            // it is guaranteed to reach its finally before the primitives go away.
            if (_checksInFlight > 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _drained.Task;
            }
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        // Waiting on the loop alone disposed _shutdown and _oneAtATime out from under
        // a CheckNowAsync the UI had fired and forgotten, which then threw
        // ObjectDisposedException from the token or from its own semaphore release.
        // So wait for the in-flight checks too - but for a bounded time.
        //
        // Nothing here is guaranteed to end when the token is cancelled. CheckAsync
        // reaches HttpClient (a 60-second timeout) and DataInstaller (file I/O that
        // takes no token at all), so an unbounded wait is a hang, and it is a hang on
        // the UI thread: TrayContext.Dispose blocks on this after clearing the tray
        // icon, and Program.Main still holds the single-instance mutex, so the user
        // sees no window, no icon, and a relaunch that does nothing. A logged check
        // finishing into a disposed service is the smaller failure of the two.
        var pending = loop is null
            ? drained
            : drained is null
                ? loop
                : Task.WhenAll(loop, drained);

        var quiesced = true;

        if (pending is not null)
        {
            try
            {
                await pending.WaitAsync(QuiesceTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                quiesced = false;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        if (!quiesced)
        {
            // Deliberately not disposed. The check that is still running holds the
            // token and the semaphore, and disposing them here is the crash this wait
            // was added to prevent. Neither type allocates anything the finalizer
            // cannot reclaim - no wait handle is ever asked for - so leaving them to
            // the GC costs nothing, and the process is on its way out regardless.
            Log.Warn("a check was still running at shutdown; leaving it to finish on its own.");
            return;
        }

        _shutdown.Dispose();
        _oneAtATime.Dispose();
    }
}
