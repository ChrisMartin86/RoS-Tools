using System.Net;
using System.Text;
using RoSTools.Sidecar.Core;
using RoSTools.Sidecar.Core.Blizzard;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// Regressions for the defects an adversarial review of this feature turned up.
/// Every one of them ended with a wrong roster installed - and therefore announced
/// to the whole guild by <c>Core/Sync.lua</c> - while the app reported success.
/// </summary>
public class PullGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-guard-" + Guid.NewGuid().ToString("N"));

    private readonly string _addOn;
    private readonly string _destination;
    private readonly SettingsStore _store;

    private static readonly BlizzardCredentials Credentials = new("client-id-0123456789", "secret", "us");
    private static readonly GuildIdentity Riddle = new("us", "khadgar", "riddle-of-steel");

    public PullGuardTests()
    {
        _addOn = Path.Combine(_root, "Interface", "AddOns", "RoS-Tools");
        Directory.CreateDirectory(Path.Combine(_addOn, "Data"));
        File.WriteAllText(Path.Combine(_addOn, AddOnLocator.TocFileName), "## Interface: 120000");

        _destination = AddOnLocator.DataFileFor(_addOn);
        Log.DirectoryOverride = Path.Combine(_root, "logs");

        _store = new SettingsStore(Path.Combine(_root, "sidecar.json"));
        _store.Load();
        _store.Update(s => s.AddOnPath = _addOn);
    }

    private PullService Service(BlizzardStub stub) =>
        new(_store, region => new BlizzardApiClient(region, stub));

    private void Install(int count, GuildIdentity? identity = null, long? epoch = null)
    {
        var entries = Enumerable.Range(1, count)
            .Select(i => new RosterEntry($"Char{i:D3}-khadgar", 250 + (i % 60)));

        GuildDataWriter.WriteTo(
            _destination,
            GuildDataWriter.Render(
                entries,
                identity ?? Riddle,
                epoch ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600,
                out _));
    }

    /// <summary>
    /// With no learned guild in settings, the pull path used to run no identity check
    /// at all - so a typo in the realm box installed another guild's roster over a
    /// perfectly good one. That client then held the highest epoch in the guild while
    /// every peer rejected the snapshot on identity, silently, forever; and having
    /// learned the wrong guild it would refuse the legitimate CI file from then on.
    /// </summary>
    [Fact]
    public async Task A_pull_for_another_guild_is_refused_from_the_installed_file_alone()
    {
        Install(50);

        // Exactly the state a fresh sidecar beside an existing addon is in.
        Assert.Null(_store.Current.Guild);

        var stub = BlizzardStub.WithRoster(50);
        var result = await Service(stub)
            .PullAsync(Credentials, new PullRequest("us", "Khadgar", "Some Other Guild"));

        Assert.False(result.Ok);
        Assert.Contains("could never be installed", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, stub.RosterCalls);
        Assert.Equal(Riddle, GuildDataValidator.IdentityOf(_destination));
    }

    /// <summary>The install-time check has the same fallback, in case a roster
    /// appears between the pull and the install.</summary>
    [Fact]
    public async Task Install_refuses_a_pull_that_no_longer_matches_the_installed_guild()
    {
        var service = Service(BlizzardStub.WithRoster(20));
        Assert.True((await service.PullAsync(Credentials, Request("Riddle of Steel"))).Ok);

        // A different guild's roster lands at the destination in the meantime.
        Install(20, new GuildIdentity("us", "khadgar", "another-guild"));

        var outcome = service.Install(overrideShrink: false);

        Assert.False(outcome.Ok);
        Assert.Contains("Refused", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("another-guild", GuildDataValidator.IdentityOf(_destination)!.Guild);
    }

    /// <summary>
    /// A realm or guild whose slug carries a quote or a brace would be written into a
    /// Lua string literal. The validator catches every such file, but only after a
    /// full ~180-call pull, and reports it as a writer bug.
    /// </summary>
    [Theory]
    [InlineData("Bad\"Realm")]
    [InlineData("Brace{Realm")]
    [InlineData("Back\\slash")]
    [InlineData("Equals=Realm")]
    public async Task An_unusable_realm_is_refused_before_any_api_call(string realm)
    {
        var stub = BlizzardStub.WithRoster(5);

        var result = await Service(stub).PullAsync(Credentials, new PullRequest("us", realm, "Riddle of Steel"));

        Assert.False(result.Ok);
        Assert.Contains("not a usable", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, stub.RosterCalls);
    }

    /// <summary>
    /// A pull that reports 180 characters but wrote 150 made the page's own shrink
    /// warning disagree with the server's, and hid genuinely removed characters from
    /// the review screen that exists precisely to catch that.
    /// </summary>
    [Fact]
    public async Task The_reported_roster_is_what_the_file_actually_contains()
    {
        var stub = BlizzardStub.WithRoster(10);

        // Three names Core/Sync.lua could not carry: dropped by the writer, so they
        // must not appear in the reported entry list either.
        stub.Add("Has Space", "khadgar", 80, 300);
        stub.Add("Pipe|Name", "khadgar", 80, 300);
        stub.Add("Colon:Name", "khadgar", 80, 300);

        var result = await Service(stub).PullAsync(Credentials, Request());

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, result.DroppedKeys.Count);
        Assert.Equal(10, result.Entries.Count);
        Assert.Equal(result.Validation!.Entries, result.Entries.Count);
        Assert.DoesNotContain(result.Entries, e => e.Key.Contains('|', StringComparison.Ordinal));
    }

    /// <summary>
    /// Dropped keys count against the shrink floor, because they are genuinely gone
    /// from the file the guild would adopt.
    /// </summary>
    [Fact]
    public async Task Dropped_keys_count_towards_the_shrink_guard()
    {
        Install(20);

        var stub = new BlizzardStub();
        for (var i = 1; i <= 10; i++)
        {
            stub.Add($"Char{i:D3}", "khadgar", 80, 280);
        }

        for (var i = 11; i <= 20; i++)
        {
            stub.Add($"Bad Name{i}", "khadgar", 80, 280);
        }

        var service = Service(stub);
        Assert.True((await service.PullAsync(Credentials, Request())).Ok);

        var outcome = service.Install(overrideShrink: false);

        Assert.False(outcome.Ok);
        Assert.True(outcome.NeedsOverride);
    }

    /// <summary>
    /// The shrink baseline has to be a roster the addon could actually load. A
    /// destination the validator rejects is not data worth protecting, and using its
    /// entry count refused good pulls against a file nobody can read.
    /// </summary>
    [Fact]
    public async Task A_corrupt_installed_roster_is_not_a_shrink_baseline()
    {
        Install(100);

        // Damage outside the tables: still brace-balanced, still parses as two
        // tables, and not something WoW will load. The skeleton check catches it.
        File.WriteAllText(_destination,
            File.ReadAllText(_destination).Replace("local _, ns = ...", "local _, ns = ...\nbroken junk"));

        Assert.False(GuildDataValidator.Validate(_destination).Ok);

        var service = Service(BlizzardStub.WithRoster(30));
        Assert.True((await service.PullAsync(Credentials, Request())).Ok);

        var outcome = service.Install(overrideShrink: false);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(30, GuildDataValidator.Validate(_destination).Entries);
    }

    /// <summary>
    /// Cancelling used to let an OperationCanceledException escape PullAsync, leaving
    /// Last holding the PREVIOUS pull and Progress frozen mid-count. The page then
    /// stopped polling and offered that stale roster for install, with no error shown.
    /// </summary>
    [Fact]
    public async Task A_cancelled_pull_reports_failure_instead_of_leaving_the_previous_one_staged()
    {
        var service = Service(BlizzardStub.WithRoster(5));

        var first = await service.PullAsync(Credentials, Request());
        Assert.True(first.Ok, first.Error);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var second = await service.PullAsync(Credentials, Request(), cancelled.Token);

        Assert.False(second.Ok);
        Assert.Equal(PullPhase.Failed, service.Progress.Phase);
        Assert.False(service.Last!.Ok);

        // And nothing stale is left installable.
        Assert.False(service.Install(overrideShrink: false).Ok);
    }

    /// <summary>
    /// Core/Sync.lua orders the guild by generated_epoch, so reinstalling an older
    /// export drops this client below data its own peers already adopted from it.
    /// Before the console existed the branch file only moved forwards; now a manual
    /// pull can be newer than what CI published - which is why someone would run one.
    /// </summary>
    [Fact]
    public async Task The_poller_will_not_replace_a_newer_roster_with_an_older_one()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Install(30, epoch: now - 60);

        var older = GuildDataWriter.Render(
            Enumerable.Range(1, 30).Select(i => new RosterEntry($"Char{i:D3}-khadgar", 100)),
            Riddle,
            now - 604800,
            out _);

        var result = await new UpdateService(_store, new GuildDataClient(Serving(older)))
            .CheckAsync(force: true);

        Assert.Equal(UpdateOutcome.AlreadyCurrent, result.Outcome);
        Assert.Contains("newer than the published one", result.Message, StringComparison.Ordinal);

        // Untouched: item levels are still the pulled ones, not the branch file's.
        var installed = GuildDataValidator.EntriesOf(_destination)!;
        Assert.All(installed, e => Assert.NotEqual(100, e.Value));
    }

    [Fact]
    public async Task The_poller_still_installs_a_newer_roster()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Install(30, epoch: now - 604800);

        var newer = GuildDataWriter.Render(
            Enumerable.Range(1, 30).Select(i => new RosterEntry($"Char{i:D3}-khadgar", 400)),
            Riddle,
            now - 60,
            out _);

        var result = await new UpdateService(_store, new GuildDataClient(Serving(newer)))
            .CheckAsync(force: true);

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.All(GuildDataValidator.EntriesOf(_destination)!, e => Assert.Equal(400, e.Value));
    }

    private static PullRequest Request(string guild = "Riddle of Steel") =>
        new("us", "Khadgar", guild);

    /// <summary>A data source that always answers 200 with <paramref name="body"/>.
    /// UpdateServiceTests has its own richer stub, but it is private to that class
    /// and these tests only ever need the happy path.</summary>
    private static HttpMessageHandler Serving(string body) => new AlwaysOk(body);

    private sealed class AlwaysOk(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            });
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
