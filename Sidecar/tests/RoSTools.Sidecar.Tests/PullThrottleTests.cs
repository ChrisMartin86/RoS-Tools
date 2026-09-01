using System.Text.Json;
using RoSTools.Sidecar.Core;
using RoSTools.Sidecar.Core.Blizzard;
using RoSTools.Sidecar.Core.Web;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// The server-side minimum interval between pulls.
/// <para>
/// The design's stated aim is that CI stays the guild's single <i>scheduled</i>
/// consumer of the Blizzard API, and the console's pull is the one deliberate
/// exception, on a human click. Until this existed, that property rested entirely on
/// a disabled button in a page anyone with the token can bypass and an in-memory
/// flag that a restart clears. Nothing on the server said "not again this soon", and
/// nothing survived a restart at all.
/// </para>
/// </summary>
public class PullThrottleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-throttle-" + Guid.NewGuid().ToString("N"));

    private readonly string _addOn;
    private readonly SettingsStore _store;

    private const string PullBody = """{"region":"us","realm":"Khadgar","guild":"Riddle of Steel"}""";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly BlizzardCredentials Credentials = new("client-id-0123456789", "secret", "us");
    private static readonly PullRequest Request = new("us", "Khadgar", "Riddle of Steel");

    public PullThrottleTests()
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
            s.BlizzardClientId = "0123456789abcdef0123456789abcdef";
            s.BlizzardClientSecretProtected = new PassthroughSecretProtector().Protect("secret");
            s.BlizzardRegion = "us";
        });
    }

    [Fact]
    public async Task A_second_pull_inside_the_window_is_refused_and_says_when_the_next_one_is_allowed()
    {
        var stub = BlizzardStub.WithRoster(6);
        var pulls = Service(stub, TimeSpan.FromMinutes(30));
        var api = new ConsoleApi(_store, pulls, new PassthroughSecretProtector());

        Assert.Equal(202, (await api.HandleAsync("/api/pull", "POST", PullBody, default)).Status);
        await WaitUntilIdleAsync(api);
        Assert.Equal(1, stub.RosterCalls);

        var (status, body) = await api.HandleAsync("/api/pull", "POST", PullBody, default);

        Assert.Equal(429, status);

        var error = Json(body).GetProperty("error").GetString()!;
        Assert.Contains("one every 30 minutes", error, StringComparison.Ordinal);
        Assert.Contains("The next one is allowed at", error, StringComparison.Ordinal);
        Assert.Contains("UTC", error, StringComparison.Ordinal);

        // Refused on the server, not merely greyed out on the page: no second roster
        // call, and the pull slot is free again for when the window opens.
        Assert.Equal(1, stub.RosterCalls);
        Assert.False(pulls.IsRunning);
    }

    /// <summary>
    /// The whole point of persisting it. An in-memory interval is defeated by
    /// restarting the app, which is exactly what a crash loop or a script does.
    /// </summary>
    [Fact]
    public async Task The_interval_survives_a_restart()
    {
        var stub = BlizzardStub.WithRoster(6);
        var first = Service(stub, TimeSpan.FromMinutes(30));

        Assert.True((await first.PullAsync(
            new BlizzardCredentials("client-id-0123456789", "secret", "us"),
            new PullRequest("us", "Khadgar", "Riddle of Steel"))).Ok);

        Assert.True(File.Exists(first.StampPath));

        // A brand new service over the same state directory: the sidecar restarted.
        var restarted = Service(BlizzardStub.WithRoster(6), TimeSpan.FromMinutes(30));
        var admission = restarted.TryStart();

        Assert.False(admission.Granted);
        Assert.Equal(PullRefusal.TooSoon, admission.Refusal);
        Assert.Contains("The next one is allowed at", admission.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stamp is taken once a pull is past its pre-flight checks and about to
    /// spend quota. A typo in the realm box never reaches Blizzard, so it must not
    /// cost the user the next two minutes.
    /// </summary>
    [Fact]
    public async Task A_pull_refused_before_it_reaches_blizzard_does_not_start_the_clock()
    {
        var stub = BlizzardStub.WithRoster(6);
        var pulls = Service(stub, TimeSpan.FromMinutes(30));

        var credentials = new BlizzardCredentials("client-id-0123456789", "secret", "us");

        // Refused by the region check, before the client is even built.
        var bad = await pulls.PullAsync(credentials, new PullRequest("zz", "Khadgar", "Riddle of Steel"));
        Assert.False(bad.Ok);
        Assert.False(File.Exists(pulls.StampPath));

        var good = await pulls.PullAsync(credentials, new PullRequest("us", "Khadgar", "Riddle of Steel"));
        Assert.True(good.Ok, good.Error);
        Assert.True(File.Exists(pulls.StampPath));
    }

    [Fact]
    public void The_default_interval_is_the_documented_one() =>
        Assert.Equal(TimeSpan.FromMinutes(2), new PullService(_store).MinimumInterval);

    // ------------------------------------------------------------------
    // A clock that moved
    // ------------------------------------------------------------------

    /// <summary>
    /// A machine that boots with a dead RTC, runs one pull and is then corrected by
    /// NTP wrote a stamp a year ahead of its own clock. Taken at face value that is a
    /// refusal saying "in 525602 minutes", and nothing clears it: not settings, not
    /// the console, not a restart. The stamp is a clock reading, so a stamp from the
    /// future is a clock that moved and is worth exactly one interval.
    /// </summary>
    [Fact]
    public void A_stamp_from_the_future_is_clamped_to_one_interval()
    {
        var pulls = Service(BlizzardStub.WithRoster(6), TimeSpan.FromMinutes(30));

        WriteStamp(pulls.StampPath, DateTimeOffset.UtcNow.AddYears(1));

        var admission = pulls.TryStart();

        Assert.False(admission.Granted);
        Assert.Equal(PullRefusal.TooSoon, admission.Refusal);

        // One interval, not a year.
        Assert.Contains("in 30 minutes", admission.Message!, StringComparison.Ordinal);

        // And the refusal names the file, so a throttle that is ever wrong anyway
        // still has a way out that does not involve reading the source.
        Assert.Contains(pulls.StampPath, admission.Message!, StringComparison.Ordinal);

        // Rewritten, so the lockout cannot outlive this process either.
        Assert.True(
            StampOnDisk(pulls.StampPath) < DateTimeOffset.UtcNow.AddMinutes(1),
            "the skewed stamp was left on disk for the next process to fail closed on");
    }

    /// <summary>
    /// The other half of the clamp: after one interval the pull really is allowed.
    /// Bounded, in other words, not merely reported as bounded.
    /// </summary>
    [Fact]
    public async Task A_pull_is_allowed_one_interval_after_a_future_stamp_not_a_year_after()
    {
        var pulls = Service(BlizzardStub.WithRoster(6), TimeSpan.FromMilliseconds(200));

        WriteStamp(pulls.StampPath, DateTimeOffset.UtcNow.AddYears(1));

        Assert.False(pulls.TryStart().Granted);

        await Task.Delay(400);

        var admission = pulls.TryStart();
        Assert.True(admission.Granted, admission.Message);

        pulls.Abandon(admission.Ticket!);
    }

    /// <summary>
    /// A stamp that is there but cannot be believed is not a first run - something
    /// wrote it. Failing open on it hands a crash loop an unthrottled pull on every
    /// restart, which is the scheduled second consumer of the guild's quota that this
    /// whole mechanism exists to prevent. Failing closed for one interval costs two
    /// minutes and repairs the file.
    /// </summary>
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("")]
    public void A_stamp_that_cannot_be_believed_costs_one_interval_and_is_repaired(string content)
    {
        var pulls = Service(BlizzardStub.WithRoster(6), TimeSpan.FromMinutes(30));

        Directory.CreateDirectory(Path.GetDirectoryName(pulls.StampPath)!);
        File.WriteAllText(pulls.StampPath, content);

        var admission = pulls.TryStart();

        Assert.False(admission.Granted);
        Assert.Equal(PullRefusal.TooSoon, admission.Refusal);
        Assert.Contains("in 30 minutes", admission.Message!, StringComparison.Ordinal);

        // Repaired in place: the next process reads an ordinary stamp rather than
        // failing closed all over again on the same unreadable file.
        var repaired = StampOnDisk(pulls.StampPath);
        Assert.InRange(
            repaired,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1));
    }

    /// <summary>No stamp at all is still a first run, and still opens the gate.</summary>
    [Fact]
    public void A_missing_stamp_is_a_first_run_and_is_allowed()
    {
        var pulls = Service(BlizzardStub.WithRoster(6), TimeSpan.FromMinutes(30));

        Assert.False(File.Exists(pulls.StampPath));

        var admission = pulls.TryStart();
        Assert.True(admission.Granted, admission.Message);

        pulls.Abandon(admission.Ticket!);
    }

    // ------------------------------------------------------------------
    // The convenience overload
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>PullAsync(credentials, request, ct)</c> passed only <c>TryStart().Ticket</c>,
    /// which is null for a throttle refusal and for a losing race alike - so a
    /// throttled call was answered "A pull is already running." when no pull was. Worse,
    /// it then published that lie as <c>Last</c> and <c>Progress</c>, replacing a result
    /// describing a pull that genuinely succeeded, and left the staged roster in
    /// <c>%TEMP%</c> with nothing pointing at it.
    /// </summary>
    [Fact]
    public async Task A_throttled_convenience_pull_reports_the_throttle_and_keeps_the_previous_result()
    {
        var stub = BlizzardStub.WithRoster(6);
        var pulls = Service(stub, TimeSpan.FromMinutes(30));

        var first = await pulls.PullAsync(Credentials, Request);
        Assert.True(first.Ok, first.Error);
        Assert.True(File.Exists(first.StagingPath!));

        var second = await pulls.PullAsync(Credentials, Request);

        Assert.False(second.Ok);
        Assert.Equal(PullRefusal.TooSoon, second.Refusal);
        Assert.Contains("one every 30 minutes", second.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("already running", second.Error!, StringComparison.OrdinalIgnoreCase);

        // A refusal never held the slot, so it does not get to describe the state.
        var status = pulls.Status();
        Assert.Same(first, status.Last);
        Assert.True(status.Last!.Ok);
        Assert.Equal(PullPhase.Done, status.Progress.Phase);
        Assert.False(status.Running);

        // ...and the roster it staged is still there to install.
        Assert.True(File.Exists(first.StagingPath!));
        Assert.Equal(1, stub.RosterCalls);
    }

    // ------------------------------------------------------------------
    // Where the stamp lives
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>Paths.StateDirectory</c> is built from
    /// <c>Environment.GetFolderPath(LocalApplicationData)</c>, which answers an empty
    /// string when it cannot be resolved - and the settings path, and this one with it,
    /// is then relative to the process's working directory. A throttle file that moves
    /// when something calls <c>SetCurrentDirectory</c> is no throttle: the next read
    /// finds nothing and opens the gate.
    /// </summary>
    [Fact]
    public void The_stamp_path_is_absolute_and_does_not_follow_the_working_directory()
    {
        var pulls = new PullService(new SettingsStore("sidecar.json"));

        Assert.True(
            Path.IsPathRooted(pulls.StampPath),
            $"the throttle stamp is relative to whatever the working directory is: {pulls.StampPath}");

        var before = pulls.StampPath;
        var original = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(Path.GetTempPath());
            Assert.Equal(before, pulls.StampPath);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    // ------------------------------------------------------------------
    private PullService Service(BlizzardStub stub, TimeSpan interval) =>
        new(_store, region => new BlizzardApiClient(region, stub), interval);

    private static void WriteStamp(string path, DateTimeOffset at)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { LastPullUtc = at }));
    }

    private static DateTimeOffset StampOnDisk(string path) =>
        JsonDocument.Parse(File.ReadAllText(path))
            .RootElement.GetProperty("LastPullUtc").GetDateTimeOffset();

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private static async Task WaitUntilIdleAsync(ConsoleApi api)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var (_, body) = await api.HandleAsync("/api/pull", "GET", string.Empty, default);
            if (!Json(body).GetProperty("running").GetBoolean())
            {
                return;
            }

            await Task.Delay(15);
        }

        throw new TimeoutException("a pull never finished");
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
