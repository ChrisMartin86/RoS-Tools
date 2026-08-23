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
        Assert.Equal("\"abc123\"", _store.Current.ETag);
        Assert.Null(_store.Current.LastError);
    }

    [Fact]
    public async Task A_304_writes_nothing()
    {
        File.WriteAllText(_destination, ValidRoster);
        _store.Update(s => s.ETag = "\"abc123\"");

        var written = File.GetLastWriteTimeUtc(_destination);
        var handler = StubHandler.Status(HttpStatusCode.NotModified);

        var result = await Service(handler).CheckAsync(force: false);

        Assert.Equal(UpdateOutcome.AlreadyCurrent, result.Outcome);
        Assert.Equal("\"abc123\"", handler.LastIfNoneMatch);
        Assert.Equal(written, File.GetLastWriteTimeUtc(_destination));
    }

    [Fact]
    public async Task A_missing_destination_forces_an_unconditional_fetch()
    {
        // Otherwise a cached ETag would earn a 304 forever and the roster would
        // never be restored after someone deleted or reinstalled the addon.
        _store.Update(s => s.ETag = "\"abc123\"");
        var handler = StubHandler.Ok(ValidRoster, etag: "\"abc123\"");

        var result = await Service(handler).CheckAsync(force: false);

        Assert.Null(handler.LastIfNoneMatch);
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
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
}
