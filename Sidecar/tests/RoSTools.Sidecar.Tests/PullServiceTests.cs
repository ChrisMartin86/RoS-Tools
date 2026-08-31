using System.Net;
using RoSTools.Sidecar.Core;
using RoSTools.Sidecar.Core.Blizzard;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// The pull path, end to end over a stub Blizzard.
/// <para>
/// The property that matters throughout: <b>nothing a pull produces reaches the
/// addon folder without passing the same gate a download would</b>, and a pull that
/// lost characters cannot quietly become the guild's newest roster.
/// </para>
/// </summary>
public class PullServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-pull-" + Guid.NewGuid().ToString("N"));

    private readonly string _addOn;
    private readonly string _destination;
    private readonly SettingsStore _store;

    private static readonly BlizzardCredentials Credentials = new("client-id-0123456789", "secret", "us");
    private static readonly PullRequest Request = new("us", "Khadgar", "Riddle of Steel");

    public PullServiceTests()
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

    /// <summary>Puts a roster of <paramref name="count"/> characters in the addon folder.</summary>
    private void Install(int count, GuildIdentity? identity = null)
    {
        var entries = Enumerable.Range(1, count)
            .Select(i => new RosterEntry($"Char{i:D3}-khadgar", 250 + (i % 60)));

        GuildDataWriter.WriteTo(
            _destination,
            GuildDataWriter.Render(
                entries,
                identity ?? new GuildIdentity("us", "khadgar", "riddle-of-steel"),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600,
                out _));
    }

    [Fact]
    public async Task Pulls_a_roster_and_stages_an_installable_file()
    {
        var stub = BlizzardStub.WithRoster(30);

        var result = await Service(stub).PullAsync(Credentials, Request);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(30, result.Entries.Count);
        Assert.Equal(30, result.RosterSize);
        Assert.Equal(0, result.NoProfile);
        Assert.Equal(new GuildIdentity("us", "khadgar", "riddle-of-steel"), result.Identity);
        Assert.Equal(1, stub.RosterCalls);
        Assert.Equal(30, stub.CharacterCalls);

        Assert.True(GuildDataValidator.Validate(result.StagingPath!).Ok);

        // Staged only. A pull must never write to the addon on its own.
        Assert.False(File.Exists(_destination));
    }

    [Fact]
    public async Task Install_writes_the_pulled_roster_and_records_it()
    {
        var service = Service(BlizzardStub.WithRoster(25));
        var pull = await service.PullAsync(Credentials, Request);
        Assert.True(pull.Ok, pull.Error);

        var outcome = service.Install(overrideShrink: false);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(25, outcome.Entries);
        Assert.True(GuildDataValidator.Validate(_destination).Ok);
        Assert.Equal(25, _store.Current.LastEntryCount);
        Assert.Equal(pull.GeneratedEpoch, _store.Current.LastGeneratedEpoch);
        Assert.Equal("riddle-of-steel", _store.Current.GuildName);
        Assert.Null(_store.Current.LastError);
    }

    /// <summary>
    /// The whole reason this feature needs a guard. A pull that lost a third of the
    /// guild to throttling is valid Lua, passes every validator check, and carries
    /// the newest epoch in the guild - so Core/Sync.lua would hand it to everyone.
    /// </summary>
    [Fact]
    public async Task A_pull_that_lost_characters_is_refused_without_an_override()
    {
        Install(100);

        var service = Service(BlizzardStub.WithRoster(50));
        Assert.True((await service.PullAsync(Credentials, Request)).Ok);

        var outcome = service.Install(overrideShrink: false);

        Assert.False(outcome.Ok);
        Assert.True(outcome.NeedsOverride);
        Assert.Contains("whole guild", outcome.Message, StringComparison.Ordinal);

        // Untouched: still the 100-character roster.
        Assert.Equal(100, GuildDataValidator.Validate(_destination).Entries);
    }

    [Fact]
    public async Task The_override_lets_a_genuine_shrink_through()
    {
        Install(100);

        var service = Service(BlizzardStub.WithRoster(50));
        Assert.True((await service.PullAsync(Credentials, Request)).Ok);

        var outcome = service.Install(overrideShrink: true);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(50, GuildDataValidator.Validate(_destination).Entries);
    }

    [Fact]
    public async Task A_pull_just_inside_the_floor_installs_without_an_override()
    {
        Install(100);

        var service = Service(BlizzardStub.WithRoster(85));
        Assert.True((await service.PullAsync(Credentials, Request)).Ok);

        Assert.True(service.Install(overrideShrink: false).Ok);
    }

    /// <summary>
    /// Refused before the roster call, not after ~180 character calls: the installer
    /// would reject the file anyway, and the quota is the user's.
    /// </summary>
    [Fact]
    public async Task A_pull_for_another_guild_is_refused_before_any_api_call()
    {
        _store.Update(s =>
        {
            s.GuildRegion = "us";
            s.GuildRealm = "khadgar";
            s.GuildName = "riddle-of-steel";
        });

        var stub = BlizzardStub.WithRoster(10);
        var result = await Service(stub).PullAsync(Credentials, new PullRequest("us", "Khadgar", "Some Other Guild"));

        Assert.False(result.Ok);
        Assert.Contains("could never be installed", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, stub.RosterCalls);
    }

    [Fact]
    public async Task Characters_without_a_profile_are_counted_not_fatal()
    {
        var stub = new BlizzardStub()
            .Add("Alpha", "khadgar", 80, 300)
            .Add("Beta", "khadgar", 80, null)
            .Add("Gamma", "khadgar", 80, 290);

        var result = await Service(stub).PullAsync(Credentials, Request);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(1, result.NoProfile);
        Assert.Equal(3, result.RosterSize);
    }

    [Fact]
    public async Task Min_level_filters_before_any_character_call()
    {
        var stub = new BlizzardStub()
            .Add("High", "khadgar", 80, 300)
            .Add("Low", "khadgar", 10, 60);

        var result = await Service(stub)
            .PullAsync(Credentials, Request with { MinLevel = 70 });

        Assert.True(result.Ok, result.Error);
        Assert.Single(result.Entries);
        Assert.Equal(1, stub.CharacterCalls);
    }

    [Fact]
    public async Task Bad_credentials_produce_a_readable_message()
    {
        var stub = BlizzardStub.WithRoster(5);
        stub.TokenStatus = HttpStatusCode.Unauthorized;

        var result = await Service(stub).PullAsync(Credentials, Request);

        Assert.False(result.Ok);
        Assert.Contains("rejected those credentials", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, stub.RosterCalls);
    }

    [Fact]
    public async Task A_missing_guild_produces_a_readable_message()
    {
        var stub = BlizzardStub.WithRoster(5);
        stub.RosterStatus = HttpStatusCode.NotFound;

        var result = await Service(stub).PullAsync(Credentials, Request);

        Assert.False(result.Ok);
        Assert.Contains("No guild", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throttled_character_request_is_retried()
    {
        var stub = new BlizzardStub().Add("Alpha", "khadgar", 80, 300);
        stub.CharacterFailures.Enqueue(HttpStatusCode.TooManyRequests);
        stub.CharacterFailures.Enqueue(HttpStatusCode.ServiceUnavailable);

        var result = await Service(stub).PullAsync(Credentials, Request);

        Assert.True(result.Ok, result.Error);
        Assert.Single(result.Entries);
        Assert.Equal(3, stub.CharacterCalls);
    }

    [Fact]
    public async Task The_delta_reports_what_would_change()
    {
        Install(10);

        var stub = BlizzardStub.WithRoster(10);
        stub.Add("Newcomer", "khadgar", 80, 400);

        var result = await Service(stub).PullAsync(Credentials, Request);

        Assert.True(result.Ok, result.Error);
        Assert.Contains("Newcomer-khadgar", result.Delta.Added);
        Assert.Empty(result.Delta.Removed);
    }

    [Fact]
    public async Task Install_before_a_pull_says_so_rather_than_writing_anything()
    {
        var outcome = Service(BlizzardStub.WithRoster(1)).Install(overrideShrink: false);

        Assert.False(outcome.Ok);
        Assert.False(File.Exists(_destination));
        await Task.CompletedTask;
    }

    /// <summary>
    /// The ETag cache's invariant is that a key describes what is installed where. A
    /// pulled file did not come from the data URL, so leaving the entry would let the
    /// next poll answer 304 over a file that server never sent.
    /// </summary>
    [Fact]
    public async Task Installing_a_pull_drops_the_download_cache_entry()
    {
        Install(30);

        _store.Update(s =>
        {
            var entry = s.StateForOrNew(_destination);
            entry.Url = s.DataUrl;
            entry.ETag = "\"stale\"";
            entry.Stamp = 1;
        });

        var service = Service(BlizzardStub.WithRoster(30));
        Assert.True((await service.PullAsync(Credentials, Request)).Ok);
        Assert.True(service.Install(overrideShrink: false).Ok);

        Assert.Null(_store.Current.StateFor(_destination));
    }

    [Fact]
    public async Task A_second_pull_replaces_the_first_and_cleans_up_after_it()
    {
        var service = Service(BlizzardStub.WithRoster(5));

        var first = await service.PullAsync(Credentials, Request);
        Assert.True(first.Ok, first.Error);

        var second = await service.PullAsync(Credentials, Request);
        Assert.True(second.Ok, second.Error);

        Assert.False(File.Exists(first.StagingPath!));
        Assert.True(File.Exists(second.StagingPath!));
    }

    [Fact]
    public async Task A_roster_with_no_usable_item_levels_fails_rather_than_installing_nothing()
    {
        var stub = new BlizzardStub()
            .Add("Alpha", "khadgar", 80, null)
            .Add("Beta", "khadgar", 80, null);

        var result = await Service(stub).PullAsync(Credentials, Request);

        Assert.False(result.Ok);
        Assert.Contains("without an item level", result.Error!, StringComparison.Ordinal);
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
