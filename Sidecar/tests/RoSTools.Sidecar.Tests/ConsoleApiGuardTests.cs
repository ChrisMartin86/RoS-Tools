using System.Text.Json;
using RoSTools.Sidecar.Core;
using RoSTools.Sidecar.Core.Blizzard;
using RoSTools.Sidecar.Core.Web;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// The console's own guards, exercised through <see cref="ConsoleApi.HandleAsync"/>
/// rather than by calling <see cref="PullService"/> directly.
/// <para>
/// That distinction is the point of this file. <c>PullGuardTests</c> covers
/// cancellation and identity thoroughly, but every one of its pulls is sequential and
/// goes straight to <c>PullAsync</c>, so the admission logic in <c>StartPull</c> - a
/// running check and a flag with a JSON deserialize, a settings snapshot and a DPAPI
/// <c>Unprotect</c> syscall between them - had no coverage at all. Every test here
/// puts a request inside one of those windows on purpose.
/// </para>
/// </summary>
public class ConsoleApiGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-apiguard-" + Guid.NewGuid().ToString("N"));

    private readonly string _addOn;
    private readonly string _destination;
    private readonly SettingsStore _store;

    private const string PullBody = """{"region":"us","realm":"Khadgar","guild":"Riddle of Steel"}""";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public ConsoleApiGuardTests()
    {
        _addOn = Path.Combine(_root, "Interface", "AddOns", "RoS-Tools");
        Directory.CreateDirectory(Path.Combine(_addOn, "Data"));
        File.WriteAllText(Path.Combine(_addOn, AddOnLocator.TocFileName), "## Interface: 120000");

        _destination = AddOnLocator.DataFileFor(_addOn);
        Log.DirectoryOverride = Path.Combine(_root, "logs");

        _store = new SettingsStore(Path.Combine(_root, "sidecar.json"));
        _store.Load();
        _store.Update(s =>
        {
            s.AddOnPath = _addOn;

            // Stored, not environment-supplied, so resolving them goes through the
            // protector - which is the syscall the admission window used to span.
            s.BlizzardClientId = "0123456789abcdef0123456789abcdef";
            s.BlizzardClientSecretProtected = "protected-secret";
            s.BlizzardRegion = "us";
        });
    }

    // ------------------------------------------------------------------
    // The admission window
    // ------------------------------------------------------------------

    /// <summary>
    /// The page polls <c>/api/pull</c> the instant its POST returns. If the running
    /// flag is only set once credentials have been resolved, that poll sees
    /// <c>running:false</c> beside the PREVIOUS pull's result - and renders a stale
    /// roster with a live Install button and a green "ok".
    /// </summary>
    [Fact]
    public async Task A_pull_reports_running_from_the_moment_it_is_admitted()
    {
        var protector = new GatedProtector();
        var api = Api(BlizzardStub.WithRoster(5), protector);

        protector.Arm();
        var started = Task.Run(() => api.HandleAsync("/api/pull", "POST", PullBody, default));

        try
        {
            await protector.Entered().WaitAsync(Patience);

            var (status, body) = await api.HandleAsync("/api/pull", "GET", string.Empty, default);

            Assert.Equal(200, status);
            Assert.True(
                Json(body).GetProperty("running").GetBoolean(),
                "a pull that has been admitted must report running before the page's first poll");
        }
        finally
        {
            // Always, so a failed assertion cannot strand the blocked request.
            protector.Release();
        }

        Assert.Equal(202, (await started.WaitAsync(Patience)).Status);
        await WaitUntilIdleAsync(api);
    }

    /// <summary>
    /// A double-clicked Pull button. The button is only disabled in the <c>.then</c>,
    /// so both requests are in flight before either response lands.
    /// </summary>
    [Fact]
    public async Task A_second_pull_inside_the_admission_window_is_refused()
    {
        var stub = BlizzardStub.WithRoster(5);
        var protector = new GatedProtector();
        var api = Api(stub, protector);

        protector.Arm();
        var first = Task.Run(() => api.HandleAsync("/api/pull", "POST", PullBody, default));

        try
        {
            await protector.Entered().WaitAsync(Patience);

            // On a background task with a deadline: a request that is admitted rather
            // than refused blocks in the protector alongside the first one, and this
            // has to report that as a failure rather than hang the suite.
            var second = Task.Run(() => api.HandleAsync("/api/pull", "POST", PullBody, default));
            var settled = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.True(
                ReferenceEquals(settled, second),
                "the second pull was admitted into the starting window instead of being refused");

            var (status, body) = await second;
            Assert.Equal(409, status);
            Assert.Contains("already running", body, StringComparison.Ordinal);
        }
        finally
        {
            protector.Release();
        }

        Assert.Equal(202, (await first.WaitAsync(Patience)).Status);
        await WaitUntilIdleAsync(api);

        // One pull reached Blizzard, not two.
        Assert.Equal(1, stub.RosterCalls);
    }

    /// <summary>
    /// An <c>/api/install</c> POST landing in the admission window used to take the
    /// pull's semaphore first. The queued pull's <c>WaitAsync(0)</c> then returned
    /// false without assigning <c>Last</c> or <c>Progress</c>, so the console reported
    /// <c>running:false</c> with the previous pull's result - while that previous
    /// pull's roster was quietly installed and announced to the guild.
    /// </summary>
    [Fact]
    public async Task An_install_cannot_run_while_a_pull_is_starting()
    {
        var protector = new GatedProtector();
        var api = Api(BlizzardStub.WithRoster(12), protector);

        // A completed, staged, installable pull for the install to grab.
        Assert.Equal(202, (await api.HandleAsync("/api/pull", "POST", PullBody, default)).Status);
        await WaitUntilIdleAsync(api);
        Assert.True(Json((await api.HandleAsync("/api/pull", "GET", string.Empty, default)).Body)
            .GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(_destination));

        // Now hold a second pull inside the window and race an install into it.
        protector.Arm();
        var second = Task.Run(() => api.HandleAsync("/api/pull", "POST", PullBody, default));

        try
        {
            await protector.Entered().WaitAsync(Patience);

            var (status, body) = await api
                .HandleAsync("/api/install", "POST", """{"override":false}""", default)
                .WaitAsync(Patience);

            Assert.Equal(400, status);
            Assert.Contains("A pull is running", body, StringComparison.Ordinal);
            Assert.False(
                File.Exists(_destination),
                "the previous pull's roster was installed while a new pull was starting");
        }
        finally
        {
            protector.Release();
        }

        await second.WaitAsync(Patience);
        await WaitUntilIdleAsync(api);
    }

    /// <summary>
    /// The loser of a pull race must not overwrite the winner's cancellation source.
    /// It used to: B's linked source replaced A's in the API's single field, A won the
    /// semaphore and ran the ~180-call pull, and B's <c>finally</c> nulled the field
    /// because <c>ReferenceEquals</c> was true for itself. <c>DELETE /api/pull</c>
    /// then answered <c>{"ok":true,"message":"Cancelling."}</c> while the pull ran to
    /// completion and burned the whole quota.
    /// </summary>
    [Fact]
    public async Task The_winner_of_a_pull_race_is_the_one_that_cancel_reaches()
    {
        var stub = BlizzardStub.WithRoster(40);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stub.BeforeRoster = () => release.Task;

        var api = Api(stub, new GatedProtector());

        Assert.Equal(202, (await api.HandleAsync("/api/pull", "POST", PullBody, default)).Status);
        await stub.RosterReached.Task.WaitAsync(Patience);

        // A second click, now that the winner is genuinely in flight.
        Assert.Equal(409, (await api.HandleAsync("/api/pull", "POST", PullBody, default)).Status);

        var cancel = await api.HandleAsync("/api/pull", "DELETE", string.Empty, default);
        Assert.Equal(200, cancel.Status);
        Assert.True(Json(cancel.Body).GetProperty("ok").GetBoolean());

        release.SetResult();

        var final = Json(await WaitUntilIdleAsync(api));
        var result = final.GetProperty("result");

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("cancelled", result.GetProperty("error").GetString()!, StringComparison.Ordinal);

        // The cancelled pull never got past the roster, so it never spent the quota.
        Assert.Equal(1, stub.RosterCalls);
        Assert.Equal(0, stub.CharacterCalls);
    }

    /// <summary>
    /// The page shows the Cancel button the moment its POST returns, which is before
    /// the queued pull has a cancellation source of its own. A cancel that lands in
    /// that window has to be honoured, not answered with "nothing to cancel" - the
    /// same lie the old code told, just narrower.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_pull_that_is_still_starting_is_honoured()
    {
        var stub = BlizzardStub.WithRoster(40);
        var protector = new GatedProtector();
        var api = Api(stub, protector);

        protector.Arm();
        var started = Task.Run(() => api.HandleAsync("/api/pull", "POST", PullBody, default));

        try
        {
            await protector.Entered().WaitAsync(Patience);

            var cancel = await api.HandleAsync("/api/pull", "DELETE", string.Empty, default);
            Assert.Equal(200, cancel.Status);
            Assert.True(Json(cancel.Body).GetProperty("ok").GetBoolean());
        }
        finally
        {
            protector.Release();
        }

        await started.WaitAsync(Patience);

        var result = Json(await WaitUntilIdleAsync(api)).GetProperty("result");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("cancelled", result.GetProperty("error").GetString()!, StringComparison.Ordinal);

        // And it really did stop: nothing reached Blizzard.
        Assert.Equal(0, stub.RosterCalls);
    }

    /// <summary>
    /// Answering "Cancelling." when there is nothing to cancel is how a pull that was
    /// going to run to completion got reported as cancelled.
    /// </summary>
    [Fact]
    public async Task Cancelling_with_no_pull_in_flight_says_so()
    {
        var api = Api(BlizzardStub.WithRoster(3), new GatedProtector());

        var (status, body) = await api.HandleAsync("/api/pull", "DELETE", string.Empty, default);

        Assert.Equal(409, status);
        Assert.False(Json(body).GetProperty("ok").GetBoolean());
    }

    /// <summary>A pull refused before it starts must give the slot straight back.</summary>
    [Fact]
    public async Task A_pull_refused_for_bad_input_does_not_leave_the_slot_claimed()
    {
        var api = Api(BlizzardStub.WithRoster(3), new GatedProtector());

        Assert.Equal(400, (await api.HandleAsync("/api/pull", "POST", """{"region":"us"}""", default)).Status);

        Assert.False(Json((await api.HandleAsync("/api/pull", "GET", string.Empty, default)).Body)
            .GetProperty("running").GetBoolean());

        // And a good request still works afterwards.
        Assert.Equal(202, (await api.HandleAsync("/api/pull", "POST", PullBody, default)).Status);
        await WaitUntilIdleAsync(api);
    }

    /// <summary>
    /// <c>StartPull</c> wrapped everything up to and including the <c>Task.Run</c>
    /// handover in <c>catch { pulls.Abandon(ticket); throw; }</c>. Past that
    /// <c>Task.Run</c> the ticket belongs to the pull, so a throw on the far side of it
    /// freed the slot under a live pull: <c>running</c> goes false, a second POST is
    /// admitted, and <c>/api/install</c> becomes reachable against a <c>Last</c> the
    /// running pull is about to replace and delete.
    /// <para>
    /// The real throw site was serializing a constant anonymous type, which will not
    /// throw - which is why the fix moves the whole tail out of the try and this test
    /// drives the seam that stands in that position.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_handover_leaves_the_slot_with_the_running_pull()
    {
        var stub = BlizzardStub.WithRoster(40);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stub.BeforeRoster = () => release.Task;

        var api = Api(stub, new GatedProtector());
        api.AfterPullHandover = () => throw new InvalidOperationException("the response could not be built");

        try
        {
            // The throw still reaches the caller as the catch-all's 500 - the request
            // failed, and says so.
            Assert.Equal(500, (await api.HandleAsync("/api/pull", "POST", PullBody, default)).Status);

            await stub.RosterReached.Task.WaitAsync(Patience);

            // But the pull it started is running, and still owns the slot.
            Assert.True(
                Json((await api.HandleAsync("/api/pull", "GET", string.Empty, default)).Body)
                    .GetProperty("running").GetBoolean(),
                "a failure after the handover freed the slot out from under a running pull");

            // So a second POST is refused, and the install guard - which is
            // IsRunning, exercised on its own by An_install_cannot_run_while_a_pull_is
            // _starting - still has something to refuse against.
            var second = await api.HandleAsync("/api/pull", "POST", PullBody, default);
            Assert.Equal(409, second.Status);
            Assert.Contains("already running", second.Body, StringComparison.Ordinal);
        }
        finally
        {
            release.SetResult();
        }

        await WaitUntilIdleAsync(api);

        // One pull reached Blizzard, and it finished.
        Assert.Equal(1, stub.RosterCalls);
    }

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    /// <summary>
    /// The page's shrink warning reads <c>state.shrinkFloorPercent</c> behind a
    /// <c>typeof === "number"</c> guard and falls back to 80 when it is missing. That
    /// guard is right - a failed <c>/api/state</c> must not throw on the one line that
    /// draws the warning - but it is also silent: rename this field and the page keeps
    /// drawing a floor, permanently detached from
    /// <see cref="PullService.ShrinkFloor"/>. Both ends of that contract need pinning,
    /// and <c>ConsolePageTests</c> only pins the page's.
    /// </summary>
    [Fact]
    public async Task The_state_payload_carries_the_shrink_floor_the_page_reads()
    {
        var api = Api(BlizzardStub.WithRoster(3), new GatedProtector());

        var (status, body) = await api.HandleAsync("/api/state", "GET", string.Empty, default);

        Assert.Equal(200, status);
        Assert.Equal(
            (int)(PullService.ShrinkFloor * 100),
            Json(body).GetProperty("shrinkFloorPercent").GetInt32());
    }

    // ------------------------------------------------------------------
    // Error shape
    // ------------------------------------------------------------------

    /// <summary>
    /// The catch-all returned <c>ex.Message</c> verbatim: full filesystem paths today,
    /// and an unbounded exception-message channel on the same endpoint set that
    /// handles the client secret.
    /// </summary>
    [Fact]
    public async Task An_unhandled_failure_does_not_put_the_exception_message_in_the_body()
    {
        const string Detail = @"C:\Users\chris\AppData\Local\RoS-Tools\sidecar.json";

        var api = new ConsoleApi(
            _store,
            new PullService(_store, r => new BlizzardApiClient(r, new BlizzardStub()), TimeSpan.Zero),
            new GatedProtector(),
            () => throw new InvalidOperationException(Detail));

        var (status, body) = await api.HandleAsync("/api/check", "POST", string.Empty, default);

        Assert.Equal(500, status);
        Assert.DoesNotContain(Detail, body, StringComparison.Ordinal);
        Assert.DoesNotContain("sidecar.json", body, StringComparison.Ordinal);
        Assert.Contains("sidecar log", body, StringComparison.Ordinal);

        // The detail is not lost - it is just somewhere only the machine's owner looks.
        Assert.Contains(Detail, await ReadLogAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // /api/check
    // ------------------------------------------------------------------

    /// <summary>
    /// Rapid clicks on Check now fired concurrent <see cref="UpdateService"/> checks
    /// at one destination file and one ETag cache entry. The pull path has had a
    /// guard since it existed; this one had none.
    /// </summary>
    [Fact]
    public async Task Two_checks_at_once_are_refused_rather_than_run_together()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var api = new ConsoleApi(
            _store,
            new PullService(_store, r => new BlizzardApiClient(r, new BlizzardStub()), TimeSpan.Zero),
            new GatedProtector(),
            async () =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return new UpdateResult(
                    UpdateOutcome.AlreadyCurrent, "stub", 0, null, DateTimeOffset.UtcNow);
            });

        var first = Task.Run(() => api.HandleAsync("/api/check", "POST", string.Empty, default));
        await started.Task.WaitAsync(Patience);

        var (status, body) = await api.HandleAsync("/api/check", "POST", string.Empty, default);

        Assert.Equal(409, status);
        Assert.Contains("already running", body, StringComparison.Ordinal);

        release.SetResult();
        await first.WaitAsync(Patience);

        Assert.Equal(1, calls);

        // The guard is released once the real check finishes, not left latched.
        Assert.Equal(200, (await api.HandleAsync("/api/check", "POST", string.Empty, default)).Status);
        Assert.Equal(2, calls);
    }

    // ------------------------------------------------------------------
    private ConsoleApi Api(BlizzardStub stub, ISecretProtector protector) =>
        new(_store,
            new PullService(_store, region => new BlizzardApiClient(region, stub), TimeSpan.Zero),
            protector);

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement.Clone();

    /// <summary>Polls <c>/api/pull</c> the way the page does, and returns the last body.</summary>
    private static async Task<string> WaitUntilIdleAsync(ConsoleApi api)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var (_, body) = await api.HandleAsync("/api/pull", "GET", string.Empty, default);
            if (!Json(body).GetProperty("running").GetBoolean())
            {
                return body;
            }

            await Task.Delay(15);
        }

        throw new TimeoutException("a pull never finished");
    }

    private async Task<string> ReadLogAsync()
    {
        var path = Path.Combine(Log.Directory, "sidecar.log");
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path)
            : string.Empty;
    }

    /// <summary>
    /// Stands in for DPAPI, and can be told to block inside <c>Unprotect</c> - the
    /// syscall that sat between the console's running check and its running flag.
    /// Blocking, not async, because that is what <c>ProtectedData</c> does.
    /// </summary>
    private sealed class GatedProtector : ISecretProtector
    {
        private TaskCompletionSource? _entered;
        private TaskCompletionSource? _release;

        public bool CanStoreSecrets => true;

        public void Arm()
        {
            _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task Entered() => _entered?.Task ?? Task.CompletedTask;

        public void Release() => _release?.TrySetResult();

        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string protectedValue)
        {
            var entered = _entered;
            var release = _release;

            if (entered is not null && release is not null)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }

            return protectedValue;
        }
    }

    public void Dispose()
    {
        Log.DirectoryOverride = null;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A temp folder that will not delete is not a test failure.
        }

        GC.SuppressFinalize(this);
    }
}
