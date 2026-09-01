using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoSTools.Sidecar.Core.Blizzard;

public sealed record RosterMember(string Name, string RealmSlug, int Level)
{
    public string Key => $"{Name}-{RealmSlug}";
}

public sealed class BlizzardApiException(string message) : Exception(message);

/// <summary>
/// The Blizzard Community API, client-credentials flow. A direct port of
/// <c>Tools/fetch_guild_info.py</c>'s <c>BlizzardClient</c>, so the two agree on
/// slugs, namespaces and retry behaviour.
/// <para>
/// <b>This is the one place in the sidecar that talks to Blizzard.</b> It exists
/// because the user asked for a hand-driven pull from the web console; the
/// background poller still reads only the published <c>guild-data</c> branch and
/// must keep doing so. Nothing here may ever be called from
/// <see cref="PollService"/>: the daily CI export stays the guild's single
/// scheduled API consumer, which is what keeps call volume flat as installs grow.
/// </para>
/// </summary>
public sealed partial class BlizzardApiClient : IDisposable
{
    private const string OAuthHost = "https://oauth.battle.net/token";
    private const int MaxAttempts = 5;

    private static readonly HashSet<HttpStatusCode> Retryable =
    [
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private readonly HttpClient _http;
    private readonly string _region;
    private readonly string _apiHost;

    public BlizzardApiClient(string region, HttpMessageHandler? handler = null)
    {
        if (!BlizzardCredentials.IsKnownRegion(region))
        {
            throw new ArgumentException($"'{region}' is not a supported region.", nameof(region));
        }

        _region = region;
        _apiHost = $"https://{region}.api.blizzard.com";

        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(GuildDataClient.UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Must stay in step with <c>slugify()</c> in <c>Tools/fetch_guild_info.py</c>
    /// and <c>RealmToSlug()</c> in <c>Core/Util.lua</c>. A realm slug that differs
    /// from the exporter's by one character produces keys no addon will match, and
    /// nothing downstream notices - the roster simply reads as "nobody I know".
    /// </summary>
    public static string Slugify(string value)
    {
        value = value.Replace("’", string.Empty, StringComparison.Ordinal)
                     .Replace("'", string.Empty, StringComparison.Ordinal)
                     .Replace("`", string.Empty, StringComparison.Ordinal);

        value = Whitespace().Replace(value.Trim(), "-");
        value = Dashes().Replace(value, "-");

        // Invariant, not the current culture: under tr-TR, "I" lowercases to a
        // dotless i and every realm with an I in it silently becomes a different
        // slug than CI produced.
        return value.ToLowerInvariant();
    }

    /// <summary>
    /// Mints the bearer token every other call depends on.
    /// <para>
    /// Retried on the same terms as <see cref="GetAsync"/>, and for a blunter reason:
    /// this was the one request in the client with no retry at all, so a single
    /// transient 503 from <c>oauth.battle.net</c> - the kind
    /// <see cref="Retryable"/> exists for - threw away a whole ~180-call pull before
    /// it had made one. 401 and 403 stay non-retryable: bad credentials are not a
    /// blip, and hammering the token endpoint with them is how an application gets
    /// rate-limited for real.
    /// </para>
    /// </summary>
    public async Task AuthenticateAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        var basic = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // A fresh message per attempt: HttpRequestMessage - and its content - may
            // not be sent twice, and reusing one turns a retry into
            // InvalidOperationException instead of a second request.
            using var request = new HttpRequestMessage(HttpMethod.Post, OAuthHost)
            {
                Content = new FormUrlEncodedContent([
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                ]),
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == MaxAttempts - 1)
                {
                    throw new BlizzardApiException(
                        $"could not reach Blizzard's OAuth service: {ex.Message}");
                }

                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new BlizzardApiException(
                        "Blizzard rejected those credentials. Check the client ID and secret at " +
                        "https://develop.battle.net/access/clients.");
                }

                if (Retryable.Contains(response.StatusCode))
                {
                    if (attempt == MaxAttempts - 1)
                    {
                        throw new BlizzardApiException(
                            $"Blizzard's OAuth service kept answering {(int)response.StatusCode} " +
                            $"after {MaxAttempts} attempts.");
                    }

                    await Task.Delay(Clamp(RetryAfter(response) ?? Backoff(attempt)), ct)
                        .ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new BlizzardApiException(
                        $"Blizzard's OAuth service answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                TokenResponse? token;
                try
                {
                    token = await response.Content
                        .ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    throw new BlizzardApiException("Blizzard's OAuth response was not the JSON we expected.");
                }

                if (string.IsNullOrWhiteSpace(token?.AccessToken))
                {
                    throw new BlizzardApiException("Blizzard returned no access token.");
                }

                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token.AccessToken);
                return;
            }
        }

        // Unreachable: every path above either returns or throws on the last attempt.
        throw new BlizzardApiException("Blizzard's OAuth service could not be reached.");
    }

    public async Task<IReadOnlyList<RosterMember>> GetRosterAsync(
        string realmSlug,
        string guildSlug,
        CancellationToken ct)
    {
        var path = $"/data/wow/guild/{Escape(realmSlug)}/{Escape(guildSlug)}/roster";
        var document = await GetAsync(path, "profile", ct).ConfigureAwait(false);

        if (document is null)
        {
            throw new BlizzardApiException(
                $"No guild '{guildSlug}' found on realm '{realmSlug}' in {_region}. " +
                "Check the realm and guild names.");
        }

        var members = new List<RosterMember>();

        if (!document.Value.TryGetProperty("members", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return members;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (!entry.TryGetProperty("character", out var character))
            {
                continue;
            }

            var name = character.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var level = character.TryGetProperty("level", out var l) && l.TryGetInt32(out var parsedLevel)
                ? parsedLevel
                : 0;

            // A member on a connected realm carries its own slug; falling back to the
            // guild's realm here is what the Python exporter does, and the keys have to
            // match it exactly.
            var memberRealm = realmSlug;
            if (character.TryGetProperty("realm", out var realm) &&
                realm.TryGetProperty("slug", out var slug) &&
                slug.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(slug.GetString()))
            {
                memberRealm = slug.GetString()!;
            }

            members.Add(new RosterMember(name, memberRealm, level));
        }

        return members;
    }

    /// <summary>Equipped item level, or null for a private profile or a character
    /// that has never logged in.</summary>
    public async Task<int?> GetItemLevelAsync(string realmSlug, string name, CancellationToken ct)
    {
        var path = $"/profile/wow/character/{Escape(realmSlug)}/{Escape(name.ToLowerInvariant())}";
        var document = await GetAsync(path, "profile", ct).ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        if (document.Value.TryGetProperty("equipped_item_level", out var equipped) &&
            equipped.TryGetInt32(out var equippedValue) &&
            equippedValue > 0)
        {
            return equippedValue;
        }

        if (document.Value.TryGetProperty("average_item_level", out var average) &&
            average.TryGetInt32(out var averageValue) &&
            averageValue > 0)
        {
            return averageValue;
        }

        return null;
    }

    /// <summary>Null means 404 - a real answer here, not a failure.</summary>
    private async Task<JsonElement?> GetAsync(string path, string namespaceKind, CancellationToken ct)
    {
        var url = $"{_apiHost}{path}?namespace={namespaceKind}-{_region}&locale=en_US";

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == MaxAttempts - 1)
                {
                    throw new BlizzardApiException($"could not reach {_apiHost}: {ex.Message}");
                }

                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // Retrying cannot fix this, and the token is good for 24h, so it is
                    // an expired or revoked application rather than a blip.
                    throw new BlizzardApiException(
                        $"Blizzard refused the request ({(int)response.StatusCode}). " +
                        "The access token may have expired -- try the pull again.");
                }

                if (Retryable.Contains(response.StatusCode))
                {
                    if (attempt == MaxAttempts - 1)
                    {
                        throw new BlizzardApiException(
                            $"Blizzard kept answering {(int)response.StatusCode} after {MaxAttempts} attempts.");
                    }

                    await Task.Delay(Clamp(RetryAfter(response) ?? Backoff(attempt)), ct)
                        .ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new BlizzardApiException(
                        $"Blizzard answered {(int)response.StatusCode} {response.ReasonPhrase} for {path}.");
                }

                try
                {
                    var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var parsed = JsonDocument.Parse(json);

                    // Clone: the JsonDocument owns pooled buffers and JsonElement is a
                    // view over them, so returning one past the using is a use-after-free
                    // that reads as random parse errors under load.
                    return parsed.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    throw new BlizzardApiException($"Blizzard returned unreadable JSON for {path}: {ex.Message}");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The server's own answer to "when should I come back", or null when it did not
    /// send one. Honouring it matters: guessing shorter than the server asked is how a
    /// 429 becomes a longer 429.
    /// </summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta
        ?? (response.Headers.RetryAfter?.Date is { } date
            ? (TimeSpan?)(date - DateTimeOffset.UtcNow)
            : null);

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));

    /// <summary>A server-supplied Retry-After is not trusted unbounded; a hostile or
    /// broken proxy could otherwise park the pull for hours.</summary>
    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero :
        wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait;

    /// <summary>
    /// Realm slugs and character names go into the URL path. Names are not ASCII
    /// (accents on EU realms are ordinary), and a name is ultimately user-supplied,
    /// so escaping is both a correctness fix and what stops a crafted realm value
    /// from adding path segments of its own.
    /// </summary>
    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private sealed record TokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }
    }

    public void Dispose() => _http.Dispose();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("-+")]
    private static partial Regex Dashes();
}
