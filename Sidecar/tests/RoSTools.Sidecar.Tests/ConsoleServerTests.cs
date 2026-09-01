using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RoSTools.Sidecar.Core;
using RoSTools.Sidecar.Core.Blizzard;
using RoSTools.Sidecar.Core.Web;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// The console listens on a socket every process on the machine can connect to, and
/// it can spend the user's Blizzard quota and install a roster the whole guild
/// adopts. Loopback is not an authorization boundary; the token is. These tests
/// exist to keep it that way.
/// </summary>
public class ConsoleServerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-console-" + Guid.NewGuid().ToString("N"));

    private SettingsStore _store = null!;
    private ConsoleServer _server = null!;
    private HttpClient _http = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Log.DirectoryOverride = Path.Combine(_root, "logs");

        _store = new SettingsStore(Path.Combine(_root, "sidecar.json"));
        _store.Load();

        var api = new ConsoleApi(
            _store,
            new PullService(_store, region => new BlizzardApiClient(region, new BlizzardStub())),
            new PassthroughSecretProtector());

        _server = new ConsoleServer(api);
        _server.Start();

        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_server.Port}") };
        return Task.CompletedTask;
    }

    private HttpRequestMessage Authed(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-RoS-Token", _server.Token);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    // ------------------------------------------------------------------
    // Authorization
    // ------------------------------------------------------------------
    [Fact]
    public async Task The_api_refuses_a_request_with_no_token()
    {
        var response = await _http.GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_api_refuses_a_wrong_token()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        request.Headers.Add("X-RoS-Token", new string('a', _server.Token.Length));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(request)).StatusCode);
    }

    /// <summary>A prefix of the real token must not be accepted, whatever the
    /// comparison does about length.</summary>
    [Fact]
    public async Task The_api_refuses_a_truncated_token()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        request.Headers.Add("X-RoS-Token", _server.Token[..^1]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_api_answers_a_correct_token()
    {
        var response = await _http.SendAsync(Authed(HttpMethod.Get, "/api/state"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
    }

    /// <summary>
    /// DNS rebinding: an attacker's domain can be pointed at 127.0.0.1 and the
    /// browser then sends its own Host header here.
    /// <para>
    /// In practice HTTP.sys refuses this before the handler runs - the prefix is
    /// registered for the <c>localhost</c> host specifically, so a foreign Host does
    /// not route to this listener and comes back 404. The explicit check in
    /// <c>ConsoleServer</c> is the belt to that braces; what this test pins is the
    /// property that matters, which is that a correct token plus a foreign Host does
    /// not get an answer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_request_for_another_host_is_refused_even_with_a_valid_token()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        request.Headers.Host = "attacker.example.com";
        request.Headers.Add("X-RoS-Token", _server.Token);

        var response = await _http.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("addOn", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The host check itself, exercised directly, since HTTP.sys usually gets there
    /// first and the code above would otherwise never be covered.
    /// </summary>
    [Theory]
    [InlineData("attacker.example.com", false)]
    [InlineData("attacker.example.com:8080", false)]
    [InlineData("localhost", true)]
    [InlineData("localhost:1234", true)]
    [InlineData("127.0.0.1:1234", true)]
    [InlineData("[::1]:1234", true)]
    [InlineData("", false)]
    public void The_host_check_accepts_only_loopback(string host, bool expected) =>
        Assert.Equal(expected, ConsoleServer.IsLoopbackHostForTests(host));

    /// <summary>
    /// The URL the tray hands the browser carries a bootstrap token, not the session
    /// token: Process.Start puts the URL on the browser's command line, which any
    /// process running as this user can read.
    /// </summary>
    [Fact]
    public async Task The_bootstrap_link_serves_the_page_and_delivers_the_session_token()
    {
        var url = _server.Url;
        Assert.DoesNotContain(_server.Token, url, StringComparison.Ordinal);

        var response = await _http.GetAsync(new Uri(url).PathAndQuery);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("RoS-Tools Sidecar", body, StringComparison.Ordinal);
        Assert.Contains(_server.Token, body, StringComparison.Ordinal);

        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Single use. A bootstrap link that stayed valid would be as good as the session
    /// token to anything that read the browser's command line.
    /// </summary>
    [Fact]
    public async Task A_bootstrap_link_works_exactly_once()
    {
        var path = new Uri(_server.Url).PathAndQuery;

        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Each_call_to_Url_invalidates_the_previous_link()
    {
        var first = new Uri(_server.Url).PathAndQuery;
        var second = new Uri(_server.Url).PathAndQuery;

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(second)).StatusCode);
    }

    /// <summary>
    /// The whole point of the handoff: an unauthenticated GET still gets the page
    /// (so refresh works, from sessionStorage) but must never get the token.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_page_load_does_not_leak_the_session_token()
    {
        var body = await (await _http.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("<!doctype html>", body, StringComparison.Ordinal);
        Assert.DoesNotContain(_server.Token, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_session_token_is_not_accepted_in_the_query_string()
    {
        var response = await _http.GetAsync($"/?t={_server.Token}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The page strips the token from the address bar and keeps it in
    /// sessionStorage, so a refresh arrives with no query string at all. That has to
    /// still serve the page - the script re-authenticates from storage.
    /// </summary>
    [Fact]
    public async Task A_wrong_token_in_the_query_string_is_refused_outright()
    {
        var response = await _http.GetAsync("/?t=not-the-token");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The bootstrap does not stop a process running as this user from racing the
    /// browser to the link and taking the session token out of the page body - nothing
    /// at this privilege level does. What it can do is leave a record, so a maintainer
    /// who sees "That link has already been used" can find out that something else
    /// used it, and when.
    /// </summary>
    [Fact]
    public async Task Redeeming_a_bootstrap_link_is_logged()
    {
        var path = new Uri(_server.Url).PathAndQuery;

        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(path)).StatusCode);

        var log = await ReadLogAsync();
        Assert.Contains("console bootstrap redeemed", log, StringComparison.Ordinal);
        Assert.Contains("session token was served", log, StringComparison.Ordinal);

        // ...and so is the second attempt on the same link, which is what the victim
        // of a race would see first.
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync(path)).StatusCode);

        log = await ReadLogAsync();
        Assert.Contains("console bootstrap refused", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_bootstrap_link_is_logged_too()
    {
        _ = _server.Url;

        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync("/?t=not-the-token")).StatusCode);

        Assert.Contains("did not match the outstanding one", await ReadLogAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A handler that threw AFTER <c>WriteAsync</c> had sent headers hit a second
    /// throw on <c>StatusCode</c>, which the one shared <c>catch {}</c> swallowed -
    /// without ever reaching <c>Close()</c>. The connection then leaked until HTTP.sys
    /// timed it out.
    /// </summary>
    [Fact]
    public void A_failure_setting_the_status_still_closes_the_response()
    {
        var closed = false;
        var aborted = false;

        ConsoleServer.FailAndClose(
            () => throw new InvalidOperationException("headers are already sent"),
            () => closed = true,
            () => aborted = true);

        Assert.True(closed, "the response was never closed after the status assignment threw");
        Assert.False(aborted);
    }

    /// <summary>Close can throw too, on a half-written response. Abort always frees
    /// the connection.</summary>
    [Fact]
    public void A_close_that_throws_falls_back_to_abort()
    {
        var aborted = false;

        ConsoleServer.FailAndClose(
            () => { },
            () => throw new InvalidOperationException("half-written"),
            () => aborted = true);

        Assert.True(aborted);
    }

    /// <summary>And the ordinary case still sets the status and closes once.</summary>
    [Fact]
    public void An_ordinary_failure_sets_the_status_and_closes()
    {
        var status = 0;
        var closes = 0;

        ConsoleServer.FailAndClose(() => status = 500, () => closes++, () => { });

        Assert.Equal(500, status);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task An_unknown_path_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/secrets.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.SendAsync(Authed(HttpMethod.Get, "/api/nope"))).StatusCode);
    }

    /// <summary>An unbounded read on a socket anything on the machine can open is a
    /// trivial way to exhaust this process's memory.</summary>
    [Fact]
    public async Task An_oversized_body_is_rejected_rather_than_buffered()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/credentials")
        {
            Content = new StringContent(new string('x', 200_000), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-RoS-Token", _server.Token);

        Assert.Equal(HttpStatusCode.InternalServerError, (await _http.SendAsync(request)).StatusCode);
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------
    [Fact]
    public async Task The_secret_is_stored_encrypted_and_never_returned()
    {
        var save = await _http.SendAsync(Authed(HttpMethod.Post, "/api/credentials", new
        {
            clientId = "0123456789abcdef0123456789abcdef",
            clientSecret = "super-secret-value",
            region = "us",
        }));

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // Not in the settings file...
        var onDisk = await File.ReadAllTextAsync(_store.Path);
        Assert.DoesNotContain("super-secret-value", onDisk, StringComparison.Ordinal);

        // ...and not in any response.
        var state = await (await _http.SendAsync(Authed(HttpMethod.Get, "/api/state")))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain("super-secret-value", state, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", state, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(state);
        var credentials = json.RootElement.GetProperty("credentials");
        Assert.True(credentials.GetProperty("present").GetBoolean());
        Assert.EndsWith("cdef", credentials.GetProperty("clientId").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_credentials_removes_them()
    {
        await _http.SendAsync(Authed(HttpMethod.Post, "/api/credentials", new
        {
            clientId = "0123456789abcdef0123456789abcdef",
            clientSecret = "s3cret-value-here",
            region = "us",
        }));

        await _http.SendAsync(Authed(HttpMethod.Delete, "/api/credentials"));

        Assert.Null(_store.Load().BlizzardClientSecretProtected);
    }

    [Theory]
    [InlineData("id", "secret", "us")]
    [InlineData("0123456789abcdef0123456789abcdef", "", "us")]
    [InlineData("0123456789abcdef0123456789abcdef", "secret", "cn")]
    [InlineData("0123456789abcdef0123456789abcdef", "secret", "evil.example.com")]
    public async Task Bad_credential_input_is_refused(string id, string secret, string region)
    {
        var response = await _http.SendAsync(
            Authed(HttpMethod.Post, "/api/credentials", new { clientId = id, clientSecret = secret, region }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_json_is_a_400_not_a_500()
    {
        var request = Authed(HttpMethod.Post, "/api/credentials");
        request.Content = new StringContent("{not json", Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(request)).StatusCode);
    }

    // ------------------------------------------------------------------
    // Pull
    // ------------------------------------------------------------------
    [Fact]
    public async Task A_pull_without_credentials_is_refused()
    {
        var response = await _http.SendAsync(Authed(HttpMethod.Post, "/api/pull", new
        {
            region = "us", realm = "khadgar", guild = "riddle-of-steel",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("credentials", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pull_without_a_realm_or_guild_is_refused()
    {
        var response = await _http.SendAsync(Authed(HttpMethod.Post, "/api/pull", new { region = "us" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Install_with_nothing_pulled_is_refused()
    {
        var response = await _http.SendAsync(Authed(HttpMethod.Post, "/api/install", new { @override = false }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no pulled roster", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Shutdown
    // ------------------------------------------------------------------

    /// <summary>
    /// Request handlers are started with <c>Task.Run</c> and never awaited, and every
    /// one of them holds the server's stopping token and hands it to
    /// <see cref="ConsoleApi"/> and on into a pull. <c>DisposeAsync</c> disposed the
    /// source those tokens came from while handlers were still holding them.
    /// <para>
    /// Today that mostly gets away with it, because the source is cancelled first and
    /// <c>CreateLinkedTokenSource</c> short-circuits on an already-cancelled token
    /// rather than registering. Mostly is the problem: if it ever did throw, it would
    /// throw at <c>PullService.PullAsync</c>'s <c>CreateLinkedTokenSource</c> line,
    /// which sits OUTSIDE the try that owns the ticket-releasing finally - so the pull
    /// slot would stay claimed for the life of the process and every later pull would
    /// be answered "already running". Waiting for the handlers makes that unreachable
    /// instead of unlikely.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Disposing_waits_for_a_request_that_is_still_running()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var api = new ConsoleApi(
            _store,
            new PullService(_store, r => new BlizzardApiClient(r, new BlizzardStub()), TimeSpan.Zero),
            new PassthroughSecretProtector(),
            async () =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return new UpdateResult(
                    UpdateOutcome.AlreadyCurrent, "stub", 0, null, DateTimeOffset.UtcNow);
            });

        var server = new ConsoleServer(api);
        server.Start();

        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.Port}") };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/check");
        request.Headers.Add("X-RoS-Token", server.Token);

        // Not awaited: it is inside the handler we are about to dispose underneath.
        var call = http.SendAsync(request);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var dispose = server.DisposeAsync().AsTask();

        Assert.NotSame(
            dispose,
            await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(500))));

        release.SetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        // The handler is finished, one way or another - the listener was stopped out
        // from under its response, and that is the handler's own business. What
        // matters is that it got there before the source it was holding went away.
        try
        {
            (await call.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
        }
        catch (HttpRequestException)
        {
            // The listener was stopped mid-response. Expected, and not what this is
            // about.
        }
    }

    private static async Task<string> ReadLogAsync()
    {
        var path = Path.Combine(Log.Directory, "sidecar.log");
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();

        Log.DirectoryOverride = null;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A temp folder that will not delete is not a test failure.
        }
    }
}
