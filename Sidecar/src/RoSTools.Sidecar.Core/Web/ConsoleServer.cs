using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RoSTools.Sidecar.Core.Web;

/// <summary>
/// A loopback-only HTTP server for the settings/data console.
/// <para>
/// It listens on <c>localhost</c> on an ephemeral port and refuses every request
/// that does not carry the session token minted when the process started. The tray
/// menu opens the browser at <c>http://localhost:PORT/?t=TOKEN</c>; the page moves
/// the token into <c>sessionStorage</c> and strips it from the address bar, then
/// sends it as a header on every API call.
/// </para>
/// <para>
/// Why a token at all, on a loopback socket: anything running as any user on this
/// machine can connect to it, and this console can spend the user's Blizzard quota
/// and write a file that propagates to the whole guild. Loopback is not an
/// authorization boundary and is not treated as one here.
/// </para>
/// <para>
/// Where the boundary actually is: at the user account. The token stops a process
/// running as <i>another</i> user, and it stops a web page in the browser. It does
/// not stop a process running as <i>this</i> user - that process can read the
/// browser's command line, its <c>sessionStorage</c>, or this process's memory. See
/// the bootstrap field below, which bounds and logs that exposure rather than
/// pretending to remove it.
/// </para>
/// </summary>
public sealed class ConsoleServer : IAsyncDisposable
{
    /// <summary>
    /// The token is compared with <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// over fixed-length bytes, so a wrong guess leaks no timing signal about how
    /// much of the prefix was right.
    /// </summary>
    private readonly byte[] _tokenBytes;

    /// <summary>
    /// A separate, single-use, short-lived token that buys exactly one load of the
    /// page - which is served with the session token in its body.
    /// <para>
    /// <b>What this is not.</b> It is not a defence against another process running as
    /// this user. That process can read the browser's command line, win the race to
    /// <c>GET /?t=...</c>, and take the session token straight out of the HTML - and
    /// even if it could not, it could read the browser's <c>sessionStorage</c> off
    /// disk, or read this process's memory. Same-user is inside the trust boundary
    /// here, and no token scheme moves it out; only running the sidecar as a different
    /// principal would. An earlier version of this comment claimed the bootstrap
    /// stopped a command-line reader from getting the session token. It does not, and
    /// saying so was worse than saying nothing, because it made the real boundary look
    /// like it had been handled.
    /// </para>
    /// <para>
    /// <b>What it does buy,</b> and why it is still here. The session token never
    /// appears in a URL, so it never lands anywhere a URL lands and outlives the
    /// session: browser history, a shell's history, a parent process's saved command
    /// line, EDR telemetry, a crash dump of the address bar. What does land there is a
    /// bootstrap that is dead after one use and after five minutes, so a link
    /// recovered from any of those later is inert. It bounds the window to the seconds
    /// between <c>Process.Start</c> and the browser's first request; it does not close
    /// it.
    /// </para>
    /// <para>
    /// <b>And it is now auditable.</b> Every redemption and every refused redemption is
    /// logged. A maintainer who saw "That link has already been used" can look in the
    /// log and find the redemption they did not make, with its timestamp - which is the
    /// difference between a silent compromise and a visible one.
    /// </para>
    /// </summary>
    private readonly Lock _bootstrapGate = new();
    private string? _bootstrap;
    private DateTimeOffset _bootstrapExpires;

    private readonly HttpListener _listener = new();
    private readonly ConsoleApi _api;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Taken once, from a source that is alive at the time.
    /// <para>
    /// Every request handler passes this into <see cref="ConsoleApi"/> and on into a
    /// pull. Reading <c>_stopping.Token</c> at request time meant reading a property of
    /// an object <see cref="DisposeAsync"/> may already have disposed, which throws -
    /// and it would throw inside <c>PullService.PullAsync</c> at the one statement that
    /// sits OUTSIDE the try owning the ticket-releasing finally, leaving the pull slot
    /// claimed for the life of the process. A token read before the disposal cannot.
    /// </para>
    /// </summary>
    private readonly CancellationToken _stoppingToken;

    /// <summary>
    /// The fire-and-forget request handlers that have not finished yet, so shutdown
    /// can wait for them instead of pulling the cancellation source out from under
    /// them. Pruned on every add; the console serves one browser tab, so this never
    /// holds more than a handful.
    /// </summary>
    private readonly Lock _inFlightGate = new();
    private readonly List<Task> _inFlight = [];

    private Task? _loop;
    private bool _disposed;

    public ConsoleServer(ConsoleApi api, int preferredPort = 0)
    {
        _api = api;
        _stoppingToken = _stopping.Token;

        Token = Base64Url(RandomNumberGenerator.GetBytes(32));
        _tokenBytes = Encoding.ASCII.GetBytes(Token);

        Port = Bind(preferredPort);
    }

    /// <summary>The session token. Regenerated every time the app starts.</summary>
    public string Token { get; }

    public int Port { get; }

    /// <summary>
    /// A fresh URL for the browser, carrying a single-use bootstrap token good for
    /// five minutes. Every call mints a new one, so reopening the console from the
    /// tray always works and every previously issued link is dead.
    /// </summary>
    public string Url
    {
        get
        {
            lock (_bootstrapGate)
            {
                _bootstrap = Base64Url(RandomNumberGenerator.GetBytes(32));
                _bootstrapExpires = DateTimeOffset.UtcNow.AddMinutes(5);
                return $"http://localhost:{Port}/?t={_bootstrap}";
            }
        }
    }

    /// <summary>
    /// True exactly once per issued bootstrap token, and only inside its five-minute
    /// window.
    /// <para>
    /// Every outcome is logged, success and failure alike. The session token is handed
    /// out on the success path, so this is the only record that it left the process at
    /// all; without it, a bootstrap intercepted by another local process produced
    /// exactly one visible symptom - "That link has already been used" - and no
    /// evidence anywhere of who used it.
    /// </para>
    /// </summary>
    private bool BurnBootstrap(string? supplied)
    {
        if (supplied is null)
        {
            return false;
        }

        lock (_bootstrapGate)
        {
            if (_bootstrap is null)
            {
                Log.Warn(
                    "console bootstrap refused: no link is outstanding. If you did not just " +
                    "reopen the console, this link was already redeemed by something else.");
                return false;
            }

            if (DateTimeOffset.UtcNow > _bootstrapExpires)
            {
                Log.Warn("console bootstrap refused: the link expired.");
                return false;
            }

            var expected = Encoding.ASCII.GetBytes(_bootstrap);
            var actual = Encoding.ASCII.GetBytes(supplied);

            if (actual.Length != expected.Length ||
                !CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                Log.Warn("console bootstrap refused: the link did not match the outstanding one.");
                return false;
            }

            _bootstrap = null;

            // The session token goes out in the response this authorises. Whoever
            // redeemed it holds full /api/pull and /api/install access until the app
            // restarts, so the redemption itself is the event worth recording.
            Log.Info("console bootstrap redeemed; the session token was served to that request.");
            return true;
        }
    }

    /// <summary>
    /// <c>localhost</c> rather than <c>127.0.0.1</c>: HTTP.sys requires a urlacl
    /// reservation for a literal-IP prefix, so <c>http://127.0.0.1:port/</c> throws
    /// "Access is denied" for a non-elevated process on some Windows versions, while
    /// the <c>localhost</c> prefix is exempt. Both resolve to loopback only - this
    /// is not a reachability difference, only a permissions one. The literal IP is
    /// still tried as a fallback in case a machine has the reverse problem.
    /// </summary>
    private int Bind(int preferredPort)
    {
        var attempts = preferredPort > 0
            ? new[] { preferredPort, 0, 0, 0 }
            : [0, 0, 0, 0];

        Exception? last = null;

        foreach (var candidate in attempts)
        {
            var port = candidate > 0 ? candidate : FreePort();

            foreach (var host in new[] { "localhost", "127.0.0.1" })
            {
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://{host}:{port}/");
                    _listener.Start();
                    Log.Info($"console listening on http://{host}:{port}/");
                    return port;
                }
                catch (Exception ex) when (ex is HttpListenerException or SocketException)
                {
                    last = ex;
                }
            }
        }

        throw new InvalidOperationException(
            $"Could not open a local port for the console: {last?.Message}", last);
    }

    /// <summary>
    /// Asks the OS for an unused port and immediately gives it back.
    /// <para>
    /// There is a race between releasing it and HttpListener claiming it, which is
    /// why <see cref="Bind"/> retries. HttpListener cannot bind port 0 itself, so
    /// there is no way to avoid the window entirely.
    /// </para>
    /// </summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Start() => _loop ??= Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        while (!_stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stoppingToken.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"console accept failed: {ex.Message}");
                continue;
            }

            // Fire and forget for the accept loop, but not for shutdown: one slow
            // pull must not stop the console answering /api/pull for progress, and
            // equally it must not still be holding the cancellation source when
            // DisposeAsync reaches for it. Every path inside is wrapped.
            Track(Task.Run(() => HandleSafelyAsync(context)));
        }
    }

    private void Track(Task handler)
    {
        lock (_inFlightGate)
        {
            _inFlight.RemoveAll(task => task.IsCompleted);
            _inFlight.Add(handler);
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context)
    {
        try
        {
            await HandleAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"console request failed: {ex.Message}");
            FailAndClose(context.Response);
        }
    }

    private static void FailAndClose(HttpListenerResponse response) =>
        FailAndClose(() => response.StatusCode = 500, response.Close, response.Abort);

    /// <summary>
    /// Ends a request that blew up mid-flight, and always ends it.
    /// <para>
    /// Three separate attempts, deliberately. When the handler threw AFTER
    /// <c>WriteAsync</c> had already sent headers, setting <c>StatusCode</c> throws
    /// <see cref="InvalidOperationException"/> - and one shared <c>catch</c> swallowed
    /// that without ever reaching <c>Close()</c>, leaking the connection until HTTP.sys
    /// timed it out. Whatever happens to the status code, the response gets closed.
    /// </para>
    /// <para>
    /// Taken as delegates rather than a response so the ordering can be tested at all.
    /// The failure it exists for is HTTP.sys's, and .NET's managed
    /// <see cref="HttpListener"/> - which is what runs on the Linux CI box - lets a
    /// post-write status assignment through without complaint, so no real response
    /// object can be put in the state that matters here.
    /// </para>
    /// </summary>
    internal static void FailAndClose(Action setStatus, Action close, Action abort)
    {
        try
        {
            setStatus();
        }
        catch (Exception)
        {
            // Headers are already on the wire; the status is no longer ours to set.
        }

        try
        {
            close();
        }
        catch (Exception)
        {
            // Close itself can throw on a half-written response. Abort is the last
            // resort, and it always frees the connection.
            try
            {
                abort();
            }
            catch (Exception)
            {
                // The client hung up. Nothing left to do and nothing to report.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        response.Headers["Cache-Control"] = "no-store";

        // Nothing on this page is meant to be embedded, linked out from, or read
        // cross-origin. The CSP is 'none' by default with inline style/script only,
        // because the page ships as one self-contained file with no network access
        // of its own.
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
            "connect-src 'self'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'";

        // A DNS-rebinding defence: an attacker's domain can be made to resolve to
        // 127.0.0.1, and the browser would then send its own Host header here. The
        // token already stops the request being useful, but there is no reason to
        // parse a request that was never addressed to this console.
        if (!IsLoopbackHost(request.Headers["Host"]))
        {
            await WriteAsync(response, 421, "text/plain", "Misdirected request.").ConfigureAwait(false);
            return;
        }

        if (!request.IsLocal)
        {
            await WriteAsync(response, 403, "text/plain", "Local requests only.").ConfigureAwait(false);
            return;
        }

        var path = request.Url?.AbsolutePath ?? "/";

        if (path is "/" or "/index.html")
        {
            var supplied = request.QueryString["t"];

            if (supplied is not null)
            {
                if (!BurnBootstrap(supplied))
                {
                    await WriteAsync(response, 403, "text/plain",
                        "That link has already been used or has expired. " +
                        "Reopen the console from the tray menu.")
                        .ConfigureAwait(false);
                    return;
                }

                // The one and only place the session token is handed out. The page
                // puts it straight into sessionStorage, which is scoped to this exact
                // origin - port included, unlike a cookie.
                await WriteAsync(response, 200, "text/html; charset=utf-8", ConsolePage.For(Token))
                    .ConfigureAwait(false);
                return;
            }

            // No token in the URL is the normal case for a refresh: the page keeps the
            // session token in sessionStorage and re-authenticates from there. Serve
            // the page WITHOUT a token baked in - handing one to an unauthenticated
            // GET would make the whole bootstrap dance pointless.
            await WriteAsync(response, 200, "text/html; charset=utf-8", ConsolePage.For(null))
                .ConfigureAwait(false);
            return;
        }

        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await WriteAsync(response, 404, "text/plain", "Not found.").ConfigureAwait(false);
            return;
        }

        if (!TokenMatches(request.Headers["X-RoS-Token"]))
        {
            await WriteAsync(response, 401, "application/json",
                """{"ok":false,"error":"Not authorised. Reopen the console from the tray menu."}""")
                .ConfigureAwait(false);
            return;
        }

        var (status, json) = await _api
            .HandleAsync(path, request.HttpMethod, await ReadBodyAsync(request).ConfigureAwait(false),
                _stoppingToken)
            .ConfigureAwait(false);

        await WriteAsync(response, status, "application/json; charset=utf-8", json).ConfigureAwait(false);
    }

    /// <summary>Test seam for <see cref="IsLoopbackHost"/>; HTTP.sys normally
    /// rejects a foreign Host before the handler runs, so it is otherwise
    /// unreachable from a test.</summary>
    internal static bool IsLoopbackHostForTests(string? host) => IsLoopbackHost(host);

    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        // Strip the port. IPv6 literals arrive bracketed, so cut from the last colon
        // only when it is outside the brackets.
        var name = host;
        if (name.StartsWith('['))
        {
            var end = name.IndexOf(']');
            name = end > 0 ? name[1..end] : name;
        }
        else
        {
            var colon = name.LastIndexOf(':');
            if (colon >= 0)
            {
                name = name[..colon];
            }
        }

        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               name == "127.0.0.1" ||
               name == "::1";
    }

    private bool TokenMatches(string? supplied)
    {
        if (supplied is null)
        {
            return false;
        }

        var bytes = Encoding.ASCII.GetBytes(supplied);

        // FixedTimeEquals returns false for a length mismatch without comparing, so
        // the length is not itself a secret here - the token is fixed-length.
        return bytes.Length == _tokenBytes.Length &&
               CryptographicOperations.FixedTimeEquals(bytes, _tokenBytes);
    }

    /// <summary>
    /// Reads the request body, capped. An unbounded read on a socket anything on the
    /// machine can open is a trivial way to exhaust this process's memory.
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        const int MaxBytes = 64 * 1024;

        if (!request.HasEntityBody)
        {
            return string.Empty;
        }

        var buffer = new byte[MaxBytes + 1];
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await request.InputStream
                .ReadAsync(buffer.AsMemory(read, buffer.Length - read))
                .ConfigureAwait(false);

            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return read > MaxBytes
            ? throw new InvalidOperationException("request body too large")
            : Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static async Task WriteAsync(
        HttpListenerResponse response,
        int status,
        string contentType,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);

        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;

        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception ex)
        {
            Log.Warn($"could not stop the console listener: {ex.Message}");
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(Patience).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Quitting; a listener thread that will not wind up is not worth
                // holding the process open for.
            }
        }

        // Handlers are started and not awaited, and each one holds _stoppingToken and
        // hands it to ConsoleApi and on to a pull. Disposing the source while one is
        // still in flight is a race whose losing side throws ObjectDisposedException
        // at a point PullAsync does not guard, leaving the pull slot claimed for the
        // life of the process and every later pull answered "already running". So wait
        // for them - and if they will not finish, leave the source undisposed. An
        // undisposed CancellationTokenSource at shutdown costs one finalizable object;
        // the alternative costs the feature.
        if (await DrainAsync(Patience).ConfigureAwait(false))
        {
            _stopping.Dispose();
        }
        else
        {
            Log.Warn(
                "console requests were still running at shutdown, so the console's " +
                "cancellation source was left undisposed rather than pulled out from under them.");
        }
    }

    /// <summary>How long shutdown waits, for the accept loop and again for the
    /// handlers still in flight behind it.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    private async Task<bool> DrainAsync(TimeSpan patience)
    {
        Task[] pending;

        lock (_inFlightGate)
        {
            pending = _inFlight.Where(task => !task.IsCompleted).ToArray();
        }

        if (pending.Length == 0)
        {
            return true;
        }

        try
        {
            // HandleSafelyAsync swallows everything, so WhenAll only ever faults if
            // the pool could not run one - and that is still a reason not to dispose.
            await Task.WhenAll(pending).WaitAsync(patience).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
