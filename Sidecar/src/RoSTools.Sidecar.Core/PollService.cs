namespace RoSTools.Sidecar.Core;

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

    private readonly SettingsStore _store;
    private readonly UpdateService _updates;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Random _jitter = new();

    private Task? _loop;
    private TimeSpan _backoff = TimeSpan.Zero;
    private CancellationTokenSource? _sleep;

    public PollService(SettingsStore store, UpdateService updates)
    {
        _store = store;
        _updates = updates;
    }

    public event Action? CheckStarted;

    public event Action<UpdateResult>? CheckCompleted;

    public DateTimeOffset? NextCheckUtc { get; private set; }

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>
    /// Runs a check now and reschedules the loop from this moment. Safe to call
    /// while the loop is mid-check - the second caller waits rather than racing.
    /// </summary>
    public async Task<UpdateResult> CheckNowAsync(bool force = false)
    {
        var result = await ExecuteAsync(force, _shutdown.Token).ConfigureAwait(false);
        WakeLoop();
        return result;
    }

    private async Task RunAsync()
    {
        var token = _shutdown.Token;

        try
        {
            await Task.Delay(StartupDelay, token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                var result = await ExecuteAsync(force: false, token).ConfigureAwait(false);

                _backoff = result.IsFailure
                    ? (_backoff == TimeSpan.Zero
                        ? MinimumBackoff
                        : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaximumBackoff.Ticks)))
                    : TimeSpan.Zero;

                await SleepAsync(NextDelay(), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Log.Error("the poll loop stopped unexpectedly", ex);
        }
    }

    private TimeSpan NextDelay()
    {
        if (_backoff > TimeSpan.Zero)
        {
            return _backoff;
        }

        var baseline = TimeSpan.FromHours(_store.Current.EffectivePollHours);

        // +/-10%, so installs spread out instead of stampeding on the hour.
        var spread = 1.0 + ((_jitter.NextDouble() - 0.5) / 5.0);
        return TimeSpan.FromTicks((long)(baseline.Ticks * spread));
    }

    private async Task SleepAsync(TimeSpan delay, CancellationToken token)
    {
        NextCheckUtc = DateTimeOffset.UtcNow + delay;

        using var sleep = CancellationTokenSource.CreateLinkedTokenSource(token);
        _sleep = sleep;

        try
        {
            await Task.Delay(delay, sleep.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Woken by CheckNowAsync; fall through and reschedule.
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
                DateTimeOffset.UtcNow);

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
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _shutdown.Dispose();
        _oneAtATime.Dispose();
    }
}
