using System.Net;
using RoSTools.Sidecar.Core;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// The loop had no tests at all, which is how an inverted backoff comparison and a
/// duplicated manual check both shipped. Everything here drives the real loop over
/// a fake clock, so the schedule is asserted rather than waited for.
/// </summary>
public class PollServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-poll-" + Guid.NewGuid().ToString("N"));

    private readonly string _addOn;
    private readonly SettingsStore _store;

    public PollServiceTests()
    {
        _addOn = Path.Combine(_root, "Interface", "AddOns", "RoS-Tools");
        Directory.CreateDirectory(Path.Combine(_addOn, "Data"));
        File.WriteAllText(Path.Combine(_addOn, AddOnLocator.TocFileName), "## Interface: 120000");

        Log.DirectoryOverride = Path.Combine(_root, "logs");

        _store = new SettingsStore(Path.Combine(_root, "sidecar.json"));
        _store.Load();
        _store.Update(s =>
        {
            s.AddOnPath = _addOn;
            s.PollIntervalHours = 6;
        });
    }

    private static string ValidRoster => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "valid.lua"));

    private PollService Poll(HttpMessageHandler handler, IPollClock clock) =>
        new(_store, new UpdateService(_store, new GuildDataClient(handler)), clock);

    // ------------------------------------------------------------------
    // Backoff
    // ------------------------------------------------------------------

    [Fact]
    public async Task Backing_off_after_failures_never_polls_more_often_than_success()
    {
        // The comparison was min(), not max(). MinimumBackoff is 5 minutes,
        // MaximumBackoff an hour and the shortest configurable interval an hour, so
        // the backoff could never exceed the baseline and the branch only ever
        // *shortened* the delay. One 404 or one refused export turned a 6-hourly
        // client into a 5-minute one settling at hourly - on every maintainer
        // machine, with the jitter window shrunk to match.
        var clock = new FakeClock();
        var handler = new SwitchableHandler(ValidRoster);
        await using var poll = Poll(handler, clock);

        poll.Start();
        await clock.WaitForDelayAsync();     // the 30-second startup delay
        clock.Elapse();                      // ... run the first check: a success

        await clock.WaitForDelayAsync();
        var afterSuccess = clock.Delays[^1];

        handler.Fail = true;

        var afterFailures = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            clock.Elapse();
            await clock.WaitForDelayAsync();
            afterFailures.Add(clock.Delays[^1]);
        }

        var configured = TimeSpan.FromHours(6);

        // Jitter is +/-10%, so 0.85 is comfortably below any legitimate value and
        // comfortably above the five-minute floor the bug produced.
        Assert.All(afterFailures, delay => Assert.True(
            delay >= configured * 0.85,
            $"a failing poll was scheduled {delay} ahead, inside the configured {configured}"));

        Assert.All(afterFailures, delay => Assert.True(
            delay >= afterSuccess * 0.8,
            $"a failing poll ({delay}) was scheduled sooner than a succeeding one ({afterSuccess})"));
    }

    [Fact]
    public async Task A_success_after_failures_stays_on_the_configured_interval()
    {
        var clock = new FakeClock();
        var handler = new SwitchableHandler(ValidRoster) { Fail = true };
        await using var poll = Poll(handler, clock);

        poll.Start();
        await clock.WaitForDelayAsync();

        for (var i = 0; i < 3; i++)
        {
            clock.Elapse();
            await clock.WaitForDelayAsync();
        }

        handler.Fail = false;
        clock.Elapse();
        await clock.WaitForDelayAsync();

        var configured = TimeSpan.FromHours(6);
        Assert.InRange(clock.Delays[^1], configured * 0.85, configured * 1.15);
    }

    // ------------------------------------------------------------------
    // Manual checks
    // ------------------------------------------------------------------

    [Fact]
    public async Task One_manual_check_runs_exactly_one_check()
    {
        // CheckNowAsync ran a check and then woke the loop; the loop swallowed the
        // cancellation and fell straight into the next iteration's check. The user
        // saw "Updated: 187 characters..." overwritten by "Already up to date." a
        // second later, and two HTTP requests went out per click.
        var clock = new FakeClock();
        var started = 0;
        await using var poll = Poll(new SwitchableHandler(ValidRoster), clock);
        poll.CheckStarted += () => Interlocked.Increment(ref started);

        poll.Start();
        await clock.WaitForDelayAsync();
        clock.Elapse();
        await clock.WaitForDelayAsync();

        Assert.Equal(1, Volatile.Read(ref started));

        await poll.CheckNowAsync();

        // The loop must come back round to a *sleep*, not to another check.
        await clock.WaitForDelayAsync();

        Assert.Equal(2, Volatile.Read(ref started));
    }

    [Fact]
    public async Task A_manual_check_reschedules_the_loop_from_now()
    {
        // What CheckNowAsync's documentation promises, and the reason waking the
        // loop is right even though re-checking is not.
        var clock = new FakeClock();
        await using var poll = Poll(new SwitchableHandler(ValidRoster), clock);

        poll.Start();
        await clock.WaitForDelayAsync();
        clock.Elapse();
        await clock.WaitForDelayAsync();

        var before = clock.Delays.Count;
        await poll.CheckNowAsync();
        await clock.WaitForDelayAsync();

        Assert.Equal(before + 1, clock.Delays.Count);
        Assert.InRange(
            clock.Delays[^1],
            TimeSpan.FromHours(6) * 0.85,
            TimeSpan.FromHours(6) * 1.15);
    }

    // ------------------------------------------------------------------
    // NextCheckUtc
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_next_check_time_is_unset_while_a_check_is_running()
    {
        // It was only ever written at the top of SleepAsync, so for the whole
        // duration of a check it pointed into the past and the tray rendered a check
        // that was already overdue and not coming.
        var clock = new FakeClock();
        await using var poll = Poll(new SwitchableHandler(ValidRoster), clock);

        DateTimeOffset? duringCheck = DateTimeOffset.UnixEpoch;
        poll.CheckStarted += () => duringCheck = poll.NextCheckUtc;

        poll.Start();
        await clock.WaitForDelayAsync();
        clock.Elapse();
        await clock.WaitForDelayAsync();

        // The second check is the interesting one: by now a sleep has set a time.
        Assert.NotNull(poll.NextCheckUtc);

        clock.Elapse();
        await clock.WaitForDelayAsync();

        Assert.Null(duringCheck);
    }

    [Fact]
    public async Task The_next_check_time_is_cleared_once_the_loop_has_stopped()
    {
        var clock = new FakeClock();
        var poll = Poll(new SwitchableHandler(ValidRoster), clock);

        poll.Start();
        await clock.WaitForDelayAsync();
        clock.Elapse();
        await clock.WaitForDelayAsync();
        Assert.NotNull(poll.NextCheckUtc);

        await poll.DisposeAsync();

        Assert.Null(poll.NextCheckUtc);
    }

    // ------------------------------------------------------------------
    // Lifetime
    // ------------------------------------------------------------------

    [Fact]
    public async Task Disposing_while_a_manual_check_is_in_flight_does_not_throw()
    {
        // CheckNowAsync is fired and forgotten from the tray and held as a delegate
        // by the console server, so a click landing during shutdown is ordinary.
        // Awaiting only the loop disposed _shutdown and _oneAtATime underneath it,
        // and the check then threw ObjectDisposedException at nobody.
        var handler = new BlockingHandler(ValidRoster);
        var poll = Poll(handler, new FakeClock());

        var check = Task.Run(() => poll.CheckNowAsync());
        Assert.True(
            await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10)),
            "the check never reached the transport");

        var dispose = poll.DisposeAsync().AsTask();
        await Task.Delay(50);
        handler.Release();

        await dispose;
        var result = await check;

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Disposing_does_not_wait_out_a_check_that_will_not_finish()
    {
        // The other half of the same shutdown. Waiting for the in-flight check with
        // no bound turned a crash into a hang, on the UI thread: CheckNowAsync is
        // fire-and-forget from the tray menu and nothing bounds what it is inside -
        // HttpClient's own timeout is sixty seconds and DataInstaller's file I/O
        // takes no token at all. Click "Check now", then "Quit" mid-fetch, and the
        // icon disappears while the process lives on for up to a minute with no
        // window and no icon, still holding Program.Main's single-instance mutex - so
        // relaunching does nothing whatsoever in that window.
        var handler = new BlockingHandler(ValidRoster);
        var poll = Poll(handler, new FakeClock());

        var check = Task.Run(() => poll.CheckNowAsync());
        Assert.True(
            await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10)),
            "the check never reached the transport");

        var dispose = poll.DisposeAsync().AsTask();

        // Generous: the cap is two seconds, matching TrayContext's console quiesce.
        // The transport stays parked for all of it, exactly as a wedged request would.
        var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)));

        try
        {
            Assert.Same(dispose, finished);
        }
        finally
        {
            handler.Release();

            // And the check it gave up on still finishes normally: the primitives it
            // is holding must not have been disposed underneath it.
            Assert.NotNull(await check);
            await dispose;
        }
    }

    [Fact]
    public async Task A_check_started_after_dispose_reports_the_shutdown_rather_than_throwing()
    {
        var poll = Poll(new SwitchableHandler(ValidRoster), new FakeClock());
        await poll.DisposeAsync();

        var result = await poll.CheckNowAsync();

        Assert.True(result.IsFailure);
        Assert.Contains("shutting down", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var poll = Poll(new SwitchableHandler(ValidRoster), new FakeClock());
        await poll.DisposeAsync();
        await poll.DisposeAsync();
    }

    [Fact]
    public async Task Start_is_idempotent_under_concurrent_callers()
    {
        // `_loop ??= Task.Run(...)` is a read, a branch and a write, so two threads
        // could each start a loop and only one of them would ever be awaited on
        // dispose - the other going on checking, and writing settings, after the app
        // believed it had stopped.
        //
        // Honest about what this is: a stress guard, not a witness. The window is a
        // couple of instructions wide, so it does not fail reliably against the
        // unsynchronised version - the fix is the lock in Start(), and this pins the
        // invariant rather than reproducing the race.
        var clock = new FakeClock();
        await using var poll = Poll(new SwitchableHandler(ValidRoster), clock);

        var threads = Math.Max(4, Environment.ProcessorCount * 2);
        using var barrier = new Barrier(threads);

        var racers = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(
            () =>
            {
                barrier.SignalAndWait();
                poll.Start();
            },
            TaskCreationOptions.LongRunning)).ToArray();

        await Task.WhenAll(racers);
        await clock.WaitForDelayAsync();
        await Task.Delay(100);

        // One loop means one startup delay. A second loop shows up here the moment
        // it asks the clock for one.
        Assert.Single(clock.Delays);
    }

    [Fact]
    public async Task Starting_after_dispose_does_not_begin_a_loop()
    {
        var clock = new FakeClock();
        var poll = Poll(new SwitchableHandler(ValidRoster), clock);

        await poll.DisposeAsync();
        poll.Start();
        await Task.Delay(100);

        Assert.Empty(clock.Delays);
        Assert.Null(poll.NextCheckUtc);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Log.DirectoryOverride = null;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    // ------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------

    /// <summary>
    /// A clock the test drives by hand. Every <see cref="DelayAsync"/> records what
    /// was asked for and then blocks until <see cref="Elapse"/> is called or the
    /// token is cancelled, so the loop advances one step per call and nothing in
    /// this file depends on wall-clock timing.
    /// </summary>
    private sealed class FakeClock : IPollClock
    {
        private readonly SemaphoreSlim _entered = new(0);
        private readonly Lock _gate = new();
        private readonly List<TimeSpan> _delays = [];

        private TaskCompletionSource? _pending;

        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

        public IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (_gate)
                {
                    return _delays.ToArray();
                }
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = token.Register(() => pending.TrySetCanceled(token));

            lock (_gate)
            {
                _delays.Add(delay);
                _pending = pending;
            }

            _entered.Release();
            return WaitAsync(pending, registration);
        }

        /// <summary>Blocks until the service asks for its next delay.</summary>
        public async Task WaitForDelayAsync()
        {
            if (!await _entered.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false))
            {
                throw new TimeoutException("the poll loop never reached its next delay");
            }
        }

        /// <summary>Runs the pending delay out.</summary>
        public void Elapse()
        {
            TaskCompletionSource? pending;

            lock (_gate)
            {
                pending = _pending;
                _pending = null;
                UtcNow += _delays[^1];
            }

            Assert.NotNull(pending);
            pending.TrySetResult();
        }

        private static async Task WaitAsync(TaskCompletionSource pending, CancellationTokenRegistration registration)
        {
            using (registration)
            {
                await pending.Task.ConfigureAwait(false);
            }
        }
    }

    /// <summary>Serves a roster, or a 404, depending on <see cref="Fail"/>.</summary>
    private sealed class SwitchableHandler(string body) : HttpMessageHandler
    {
        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    /// <summary>
    /// Parks the request until the test lets it go, deliberately ignoring the
    /// cancellation token so a shutdown cannot unblock it from the other side.
    /// </summary>
    private sealed class BlockingHandler(string body) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SemaphoreSlim Entered { get; } = new(0);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.Release();
            await _release.Task.ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
        }
    }
}
