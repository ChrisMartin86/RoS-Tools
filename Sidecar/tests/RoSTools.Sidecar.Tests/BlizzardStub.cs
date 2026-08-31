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

    public int RosterCalls { get; private set; }

    public int CharacterCalls { get; private set; }

    /// <summary>Status codes to answer character requests with before succeeding,
    /// one per queued entry. Used to exercise the retry path.</summary>
    public Queue<HttpStatusCode> CharacterFailures { get; } = new();

    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    public HttpStatusCode RosterStatus { get; set; } = HttpStatusCode.OK;

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

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;

        if (url.Host == "oauth.battle.net")
        {
            return Task.FromResult(TokenStatus == HttpStatusCode.OK
                ? Json(HttpStatusCode.OK, """{"access_token":"stub-token","expires_in":86400}""")
                : Json(TokenStatus, """{"error":"invalid_client"}"""));
        }

        if (url.AbsolutePath.EndsWith("/roster", StringComparison.Ordinal))
        {
            RosterCalls++;

            if (RosterStatus != HttpStatusCode.OK)
            {
                return Task.FromResult(Json(RosterStatus, "{}"));
            }

            var members = _members.Select(m => new
            {
                character = new { name = m.Name, level = m.Level, realm = new { slug = m.Realm } },
            });

            return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { members })));
        }

        if (url.AbsolutePath.Contains("/profile/wow/character/", StringComparison.Ordinal))
        {
            CharacterCalls++;

            if (CharacterFailures.Count > 0)
            {
                return Task.FromResult(Json(CharacterFailures.Dequeue(), "{}"));
            }

            var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var realm = Uri.UnescapeDataString(parts[^2]);
            var name = Uri.UnescapeDataString(parts[^1]);

            var match = _ilvls.FirstOrDefault(kv =>
                string.Equals(kv.Key, $"{realm}/{name}", StringComparison.OrdinalIgnoreCase));

            if (match.Key is null)
            {
                return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
            }

            return Task.FromResult(match.Value is { } ilvl
                ? Json(HttpStatusCode.OK, $$"""{"equipped_item_level":{{ilvl}}}""")
                : Json(HttpStatusCode.NotFound, "{}"));
        }

        return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
}
