using System.Net;
using System.Text;
using System.Text.Json;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// A stand-in for Blizzard's OAuth and Community API endpoints, so the pull path
/// can be exercised end to end without a credential or a network.
/// </summary>
public sealed class BlizzardStub : HttpMessageHandler
{
    private readonly Dictionary<string, int?> _ilvls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, string Realm, int Level)> _members = [];

    private int _rosterCalls;

    private int _characterCalls;

    // PullService fans character requests out with real concurrency
    // (Parallel.ForEachAsync, MaxDegreeOfParallelism = Workers), so these
    // counters are incremented from multiple threads at once. A plain `int++`
    // is a non-atomic read-modify-write and loses updates under genuine
    // concurrency -- it stayed correct in quick local runs and undercounted
    // on a busier CI runner. Interlocked keeps every call counted.
    public int RosterCalls => _rosterCalls;

    public int CharacterCalls => _characterCalls;

    /// <summary>Status codes to answer character requests with before succeeding,
    /// one per queued entry. Used to exercise the retry path.</summary>
    public Queue<HttpStatusCode> CharacterFailures { get; } = new();

    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    public HttpStatusCode RosterStatus { get; set; } = HttpStatusCode.OK;

    private int _tokenCalls;

    public int TokenCalls => _tokenCalls;

    /// <summary>Status codes to answer the token request with before succeeding, one
    /// per queued entry. The token POST is the one request that used to have no
    /// retry at all.</summary>
    public Queue<HttpStatusCode> TokenFailures { get; } = new();

    /// <summary>
    /// Per-character status overrides, by character name. Lets a test lose a
    /// specific slice of the roster to API errors rather than to 404s - the two are
    /// counted separately, and only the first means "Blizzard is having a bad time".
    /// </summary>
    public Dictionary<string, HttpStatusCode> ForcedCharacterStatus { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Awaited before the roster response is produced, so a test can hold a pull
    /// open in a known phase instead of racing it.
    /// </summary>
    public Func<Task>? BeforeRoster { get; set; }

    /// <summary>Completed the first time a roster request arrives.</summary>
    public TaskCompletionSource RosterReached { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BlizzardStub Add(string name, string realm, int level, int? ilvl)
    {
        _members.Add((name, realm, level));
        _ilvls[$"{realm}/{name}"] = ilvl;
        return this;
    }

    /// <summary>A roster of <paramref name="count"/> members, all with item levels.</summary>
    public static BlizzardStub WithRoster(int count, string realm = "khadgar")
    {
        var stub = new BlizzardStub();
        for (var i = 1; i <= count; i++)
        {
            stub.Add($"Char{i:D3}", realm, 80, 250 + (i % 60));
        }

        return stub;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;

        if (url.Host == "oauth.battle.net")
        {
            Interlocked.Increment(ref _tokenCalls);

            if (TokenFailures.Count > 0)
            {
                return Json(TokenFailures.Dequeue(), """{"error":"temporarily unavailable"}""");
            }

            return TokenStatus == HttpStatusCode.OK
                ? Json(HttpStatusCode.OK, """{"access_token":"stub-token","expires_in":86400}""")
                : Json(TokenStatus, """{"error":"invalid_client"}""");
        }

        if (url.AbsolutePath.EndsWith("/roster", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _rosterCalls);
            RosterReached.TrySetResult();

            if (BeforeRoster is { } hold)
            {
                await hold().WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (RosterStatus != HttpStatusCode.OK)
            {
                return Json(RosterStatus, "{}");
            }

            var members = _members.Select(m => new
            {
                character = new { name = m.Name, level = m.Level, realm = new { slug = m.Realm } },
            });

            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { members }));
        }

        if (url.AbsolutePath.Contains("/profile/wow/character/", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _characterCalls);

            if (CharacterFailures.Count > 0)
            {
                return Json(CharacterFailures.Dequeue(), "{}");
            }

            var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var realm = Uri.UnescapeDataString(parts[^2]);
            var name = Uri.UnescapeDataString(parts[^1]);

            if (ForcedCharacterStatus.TryGetValue(name, out var forced))
            {
                return Json(forced, "{}");
            }

            var match = _ilvls.FirstOrDefault(kv =>
                string.Equals(kv.Key, $"{realm}/{name}", StringComparison.OrdinalIgnoreCase));

            if (match.Key is null)
            {
                return Json(HttpStatusCode.NotFound, "{}");
            }

            return match.Value is { } ilvl
                ? Json(HttpStatusCode.OK, $$"""{"equipped_item_level":{{ilvl}}}""")
                : Json(HttpStatusCode.NotFound, "{}");
        }

        return Json(HttpStatusCode.NotFound, "{}");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
}
