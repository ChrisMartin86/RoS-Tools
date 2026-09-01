using System.Net;
using RoSTools.Sidecar.Core;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// End-to-end over a stub transport. The property under test throughout: an
/// installed roster is never damaged by a check that goes wrong.
/// </summary>
public class UpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-update-" + Guid.NewGuid().ToString("N"));

    private readonly string _addOn;
    private readonly string _destination;
    private readonly SettingsStore _store;

    public UpdateServiceTests()
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

    private static string ValidRoster => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "valid.lua"));

    private UpdateService Service(StubHandler handler) =>
        new(_store, new GuildDataClient(handler));

    [Fact]
    public async Task Installs_a_valid_roster_and_caches_the_etag()
    {
        var handler = StubHandler.Ok(ValidRoster, etag: "\"abc123\"");

        var result = await Service(handler).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(4, result.Entries);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
        Assert.Equal("\"abc123\"", _store.Current.StateFor(_destination)!.ETag);
        Assert.Equal(1787509007L, _store.Current.StateFor(_destination)!.Stamp);
        Assert.Null(_store.Current.LastError);
    }

    /// <summary>
    /// Puts the store in the state a successful install would leave behind, so a
    /// test can start from "this sidecar installed that file" without running one.
    /// </summary>
    private void SeedCache(string etag = "\"abc123\"", long stamp = 1787509007L)
    {
        _store.Update(s =>
        {
            var entry = s.StateForOrNew(_destination);
            entry.Url = s.DataUrl;
            entry.ETag = etag;
            entry.Stamp = stamp;
            entry.EntryCount = 4;
            entry.GeneratedAt = "2026-08-23 18:16:47";
        });
    }

    [Fact]
    public async Task A_304_writes_nothing()
    {
        File.WriteAllText(_destination, ValidRoster);
        SeedCache();

        var written = File.GetLastWriteTimeUtc(_destination);
        var handler = StubHandler.Status(HttpStatusCode.NotModified);

        var result = await Service(handler).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.AlreadyCurrent, result.Outcome);
        Assert.Equal("\"abc123\"", handler.LastIfNoneMatch);
        Assert.Equal(written, File.GetLastWriteTimeUtc(_destination));
        Assert.Equal(4, result.Entries);
    }

    [Fact]
    public async Task A_missing_destination_forces_an_unconditional_fetch()
    {
        // Otherwise a cached ETag would earn a 304 forever and the roster would
        // never be restored after someone deleted or reinstalled the addon.
        SeedCache();
        var handler = StubHandler.Ok(ValidRoster, etag: "\"abc123\"");

        var result = await Service(handler).CheckAsync(force: false);

        Assert.Null(handler.LastIfNoneMatch);
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
    }

    [Fact]
    public async Task A_destination_overwritten_by_something_else_is_refetched()
    {
        // The bug this is here for: gating the cache on "the file exists" answers a
        // destination that a CurseForge addon update (or an interrupted script, or
        // an antivirus restore) has replaced with 304 and "Already up to date",
        // leaving the wrong roster installed under a healthy tray icon - forever, if
        // the remote bytes never change again.
        SeedCache(stamp: 1787509007L);
        File.WriteAllText(_destination, ValidRoster.Replace(
            "generated_epoch = 1787509007", "generated_epoch = 1780000000", StringComparison.Ordinal));

        var handler = StubHandler.Ok(ValidRoster, etag: "\"abc123\"");

        var result = await Service(handler).CheckAsync(force: false);

        Assert.Null(handler.LastIfNoneMatch);
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
    }

    [Fact]
    public async Task A_truncated_destination_is_refetched_rather_than_trusted()
    {
        SeedCache();
        File.WriteAllText(_destination, ValidRoster[..300]);

        var result = await Service(StubHandler.Ok(ValidRoster, etag: "\"abc123\"")).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
    }

    [Fact]
    public async Task Pointing_at_a_second_addon_folder_does_not_inherit_the_first_ones_etag()
    {
        // One global ETag made the second folder's first check a 304: the new install
        // was never written to, and the sidecar reported the *old* folder's roster as
        // current. The cache is keyed by destination precisely to stop this.
        File.WriteAllText(_destination, ValidRoster);
        SeedCache();

        var second = Path.Combine(_root, "second", "Interface", "AddOns", "RoS-Tools");
        Directory.CreateDirectory(Path.Combine(second, "Data"));
        File.WriteAllText(Path.Combine(second, AddOnLocator.TocFileName), "## Interface: 120000");
        _store.Update(s => s.AddOnPath = second);

        var handler = StubHandler.Ok(ValidRoster, etag: "\"abc123\"");
        var result = await Service(handler).CheckAsync(force: false);

        Assert.Null(handler.LastIfNoneMatch);
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(ValidRoster, File.ReadAllText(AddOnLocator.DataFileFor(second)));
    }

    [Fact]
    public async Task A_changed_data_url_does_not_reuse_the_cached_etag()
    {
        File.WriteAllText(_destination, ValidRoster);
        SeedCache();
        _store.Update(s => s.DataUrl = "https://example.invalid/other/GuildData.lua");

        var handler = StubHandler.Ok(ValidRoster, etag: "\"zzz\"");
        var result = await Service(handler).CheckAsync(force: false);

        Assert.Null(handler.LastIfNoneMatch);
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
    }

    [Fact]
    public async Task A_roster_for_another_guild_is_refused_once_one_is_installed()
    {
        // The guild-wide one. Installing this would make the client hold the highest
        // generated_epoch in the guild while serving a snapshot every peer rejects on
        // identity - burning everyone's anti-entropy window, silently, forever.
        await Service(StubHandler.Ok(ValidRoster)).CheckAsync(force: false);
        Assert.Equal("riddle-of-steel", _store.Current.GuildName);

        var otherGuild = ValidRoster.Replace(
            "guild = \"riddle-of-steel\"", "guild = \"some-other-guild\"", StringComparison.Ordinal);

        var result = await Service(StubHandler.Ok(otherGuild)).CheckAsync(force: true);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("some-other-guild", result.Message);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
    }

    [Fact]
    public async Task The_guild_is_learned_from_the_installed_file_when_the_setting_is_absent()
    {
        // Upgrade path: a sidecar that predates the identity setting already has a
        // roster on disk, and that file is the authority until the setting catches up.
        File.WriteAllText(_destination, ValidRoster);

        var otherGuild = ValidRoster.Replace(
            "guild = \"riddle-of-steel\"", "guild = \"some-other-guild\"", StringComparison.Ordinal);

        var result = await Service(StubHandler.Ok(otherGuild)).CheckAsync(force: true);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
    }

    [Fact]
    public async Task A_304_to_an_unconditional_request_is_a_failure_not_a_success()
    {
        // A misbehaving proxy or mirror. The cache was already judged untrustworthy
        // here - the destination is missing - so no validators went out, and calling
        // the answer "Already up to date" reported a healthy roster over nothing at
        // all. It also dereferenced a null cache entry doing it.
        var result = await Service(StubHandler.Status(HttpStatusCode.NotModified)).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("304", result.Message);
        Assert.False(File.Exists(_destination));
    }

    [Fact]
    public async Task A_304_answering_a_forced_check_is_a_failure_not_a_success()
    {
        // force skips the validators, so this 304 answers a request that carried
        // none - the same misbehaving proxy as above. The guard read `!cacheUsable`
        // while the request had been built from `force || !cacheUsable`, so with a
        // perfectly good cache and force set it fell through to "Already up to date"
        // and silently defeated the one thing the user explicitly asked for: a
        // check that ignores the cache.
        File.WriteAllText(_destination, ValidRoster);
        SeedCache();

        var handler = StubHandler.Status(HttpStatusCode.NotModified);
        var result = await Service(handler).CheckAsync(force: true);

        Assert.Null(handler.LastIfNoneMatch);
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("304", result.Message);
        Assert.NotNull(_store.Current.LastError);
    }

    [Fact]
    public async Task A_304_over_a_destination_we_cannot_vouch_for_is_not_reported_as_current()
    {
        SeedCache();
        File.WriteAllText(_destination, "-- not a roster any more");

        var result = await Service(StubHandler.Status(HttpStatusCode.NotModified)).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.NotNull(_store.Current.LastError);
    }

    [Fact]
    public async Task The_destination_cache_key_survives_a_settings_round_trip()
    {
        // System.Text.Json replaces the dictionary wholesale on load, so an
        // OrdinalIgnoreCase comparer set in an initializer does not survive. The keys
        // are normalized instead; this proves the cache still hits after a reload.
        await Service(StubHandler.Ok(ValidRoster, etag: "\"abc123\"")).CheckAsync(force: false);

        var reloaded = new SettingsStore(Path.Combine(_root, "sidecar.json"));
        reloaded.Load();

        Assert.NotNull(reloaded.Current.StateFor(_destination));
        Assert.Equal(1787509007L, reloaded.Current.StateFor(_destination)!.Stamp);
        Assert.Equal(
            reloaded.Current.StateFor(_destination)!.Stamp,
            reloaded.Current.StateFor(_destination.ToUpperInvariant())!.Stamp);
    }

    [Fact]
    public async Task An_export_too_old_to_share_installs_but_says_so()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-120).ToUnixTimeSeconds();
        var payload = ValidRoster.Replace(
            "generated_epoch = 1787509007", $"generated_epoch = {old}", StringComparison.Ordinal);

        var result = await Service(StubHandler.Ok(payload)).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Contains("roster sharing has stopped", result.Message);
    }

    [Fact]
    public async Task A_future_dated_export_is_refused()
    {
        File.WriteAllText(_destination, ValidRoster);
        var future = DateTimeOffset.UtcNow.AddDays(400).ToUnixTimeSeconds();
        var payload = ValidRoster.Replace(
            "generated_epoch = 1787509007",
            $"generated_epoch = {future}",
            StringComparison.Ordinal);

        var result = await Service(StubHandler.Ok(payload)).CheckAsync(force: true);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("future", result.Message);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
    }

    [Fact]
    public async Task A_404_leaves_the_installed_roster_alone()
    {
        File.WriteAllText(_destination, ValidRoster);
        var handler = StubHandler.Status(HttpStatusCode.NotFound);

        var result = await Service(handler).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("404", result.Message);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
        Assert.NotNull(_store.Current.LastError);
    }

    [Fact]
    public async Task An_html_error_page_is_refused_and_the_roster_survives()
    {
        File.WriteAllText(_destination, ValidRoster);
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-404.html"));

        var result = await Service(StubHandler.Ok(html)).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("HTML", result.Message);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
    }

    [Fact]
    public async Task A_truncated_payload_is_refused_and_the_roster_survives()
    {
        File.WriteAllText(_destination, ValidRoster);
        var truncated = ValidRoster[..420];

        var result = await Service(StubHandler.Ok(truncated)).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
    }

    [Fact]
    public async Task A_download_past_the_size_ceiling_is_refused_on_its_Content_Length()
    {
        // A mistyped DataUrl pointing at something large used to be written into
        // %TEMP% in full and then read back into memory - several times over, once
        // decoded - before the validator's export-size rule got anywhere near it.
        File.WriteAllText(_destination, ValidRoster);
        var huge = ValidRoster + new string('x', GuildDataValidator.MaxRosterBytes);

        var result = await Service(StubHandler.Ok(huge)).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("ceiling", result.Message, StringComparison.Ordinal);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
    }

    [Fact]
    public async Task A_download_past_the_size_ceiling_is_stopped_even_without_a_Content_Length()
    {
        // Content-Length is a hint: it can be absent on a chunked response, or
        // simply wrong. The copy has to count what actually arrives.
        File.WriteAllText(_destination, ValidRoster);
        var huge = ValidRoster + new string('x', GuildDataValidator.MaxRosterBytes);

        var result = await Service(StubHandler.OfUnknownLength(huge)).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("ceiling", result.Message, StringComparison.Ordinal);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
    }

    [Fact]
    public async Task A_roster_comfortably_under_the_ceiling_still_installs()
    {
        var result = await Service(StubHandler.Ok(ValidRoster)).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
    }

    [Fact]
    public async Task A_stale_configured_path_fails_loudly_instead_of_writing_elsewhere()
    {
        _store.Update(s => s.AddOnPath = Path.Combine(_root, "gone"));

        var result = await Service(StubHandler.Ok(ValidRoster)).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains(AddOnLocator.TocFileName, result.Message);
    }

    [Fact]
    public async Task A_transport_failure_is_reported_not_thrown()
    {
        File.WriteAllText(_destination, ValidRoster);

        var result = await Service(StubHandler.Throws()).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(ValidRoster, File.ReadAllText(_destination));
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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        private StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public string? LastIfNoneMatch { get; private set; }

        public static StubHandler Ok(string body, string? etag = null) => new(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };

            if (etag is not null)
            {
                response.Headers.TryAddWithoutValidation("ETag", etag);
            }

            return response;
        });

        /// <summary>A 200 whose body length the client cannot know up front.</summary>
        public static StubHandler OfUnknownLength(string body) => new(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(System.Text.Encoding.UTF8.GetBytes(body)),
            });

        public static StubHandler Status(HttpStatusCode code) => new(_ => new HttpResponseMessage(code));

        public static StubHandler Throws() =>
            new(_ => throw new HttpRequestException("name resolution failed"));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastIfNoneMatch = request.Headers.TryGetValues("If-None-Match", out var values)
                ? string.Join(string.Empty, values)
                : null;

            return Task.FromResult(_respond(request));
        }
    }

    /// <summary>Content that refuses to say how long it is, the way a chunked
    /// response does.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body, 0, body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
