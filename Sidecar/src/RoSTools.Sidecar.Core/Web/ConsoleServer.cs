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
    /// A separate, single-use, short-lived token that only buys one load of the page.
    /// <para>
    /// The session token cannot travel in the URL: <c>Process.Start</c> hands the URL
    /// to the browser as a command-line argument, and any process running as this
    /// user can read another's command line. That would give a local attacker the
    /// session token - enough to force an install of a staged roster, which
    /// <c>Core/Sync.lua</c> then spreads guild-wide. So the URL carries this instead,
    /// the page trades it for the session token in the HTML body, and it is burned on
    /// first use.
    /// </para>
    /// </summary>
    private readonly Lock _bootstrapGate = new();
    private string? _bootstrap;
    private DateTimeOffset _bootstrapExpires;

    private readonly HttpListener _listener = new();
    private readonly ConsoleApi _api;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _loop;
    private bool _disposed;

    public ConsoleServer(ConsoleApi api, int preferredPort = 0)
    {
        _api = api;

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

    /// <summary>True exactly once per issued bootstrap token, and only inside its
    /// five-minute window.</summary>
    private bool BurnBootstrap(string? supplied)
    {
        if (supplied is null)
        {
            return false;
        }

        lock (_bootstrapGate)
        {
            if (_bootstrap is null || DateTimeOffset.UtcNow > _bootstrapExpires)
            {
                return false;
            }

            var expected = Encoding.ASCII.GetBytes(_bootstrap);
            var actual = Encoding.ASCII.GetBytes(supplied);

            if (actual.Length != expected.Length ||
                !CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                return false;
            }

            _bootstrap = null;
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
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"console accept failed: {ex.Message}");
                continue;
            }

            // Fire and forget: one slow pull must not stop the console answering
            // /api/pull for progress. Every path inside is wrapped.
            _ = Task.Run(() => HandleSafelyAsync(context));
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

            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // The client hung up. Nothing to do and nothing to report.
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
                _stopping.Token)
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
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Quitting; a listener thread that will not wind up is not worth
                // holding the process open for.
            }
        }

        _stopping.Dispose();
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
