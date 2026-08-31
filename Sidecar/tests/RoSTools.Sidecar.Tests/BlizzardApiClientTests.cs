using RoSTools.Sidecar.Core.Blizzard;
using Xunit;

namespace RoSTools.Sidecar.Tests;

public class BlizzardApiClientTests
{
    /// <summary>
    /// Every expected value here was produced by running the real
    /// <c>slugify()</c> from <c>Tools/fetch_guild_info.py</c>. The two must agree
    /// exactly: a slug that differs by one character produces keys no addon matches,
    /// and nothing downstream notices - the roster just reads as "nobody I know".
    /// </summary>
    [Theory]
    [InlineData("Khadgar", "khadgar")]
    [InlineData("Argent Dawn", "argent-dawn")]
    [InlineData("Aerie Peak", "aerie-peak")]
    [InlineData("Kil'jaeden", "kiljaeden")]
    [InlineData("Kil’jaeden", "kiljaeden")]
    [InlineData("Area 52", "area-52")]
    [InlineData("  Moon  Guard  ", "moon-guard")]
    [InlineData("Twisting--Nether", "twisting-nether")]
    [InlineData("Riddle of Steel", "riddle-of-steel")]
    [InlineData("Scarlet Crusade", "scarlet-crusade")]
    [InlineData("Éonar", "éonar")]
    [InlineData("BLACKROCK", "blackrock")]
    [InlineData("Zul'jin", "zuljin")]
    [InlineData("Mal'Ganis", "malganis")]
    public void Slugify_matches_the_python_exporter(string input, string expected) =>
        Assert.Equal(expected, BlizzardApiClient.Slugify(input));

    /// <summary>
    /// The region is interpolated into the API host, so an unknown value would send
    /// a bearer token minted from the user's own secret to somebody else's server.
    /// </summary>
    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("us.api.blizzard.com/../")]
    [InlineData("cn")]
    [InlineData("")]
    [InlineData("US")]
    public void An_unknown_region_is_refused(string region)
    {
        Assert.False(BlizzardCredentials.IsKnownRegion(region));
        Assert.Throws<ArgumentException>(() => new BlizzardApiClient(region));
    }

    [Theory]
    [InlineData("us")]
    [InlineData("eu")]
    [InlineData("kr")]
    [InlineData("tw")]
    public void Known_regions_are_accepted(string region)
    {
        Assert.True(BlizzardCredentials.IsKnownRegion(region));
        using var client = new BlizzardApiClient(region, new BlizzardStub());
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("short", false)]
    [InlineData("client_id: 0123456789abcdef0123456789abcdef", false)]
    [InlineData("has spaces in it here", false)]
    public void Client_ids_are_shape_checked(string value, bool expected) =>
        Assert.Equal(expected, BlizzardCredentials.LooksLikeClientId(value));

    /// <summary>
    /// Names and realms go into the URL path and are not ASCII. Escaping is both a
    /// correctness fix for EU realms and what stops a crafted value adding path
    /// segments of its own.
    /// </summary>
    [Fact]
    public async Task Non_ascii_character_names_round_trip()
    {
        var stub = new BlizzardStub().Add("Amélie", "khadgar", 80, 305);
        using var client = new BlizzardApiClient("us", stub);

        await client.AuthenticateAsync("id", "secret", CancellationToken.None);
        var ilvl = await client.GetItemLevelAsync("khadgar", "Amélie", CancellationToken.None);

        Assert.Equal(305, ilvl);
    }

    [Fact]
    public async Task A_character_that_is_404_is_null_not_an_error()
    {
        var stub = new BlizzardStub().Add("Alpha", "khadgar", 80, 300);
        using var client = new BlizzardApiClient("us", stub);

        await client.AuthenticateAsync("id", "secret", CancellationToken.None);

        Assert.Null(await client.GetItemLevelAsync("khadgar", "Nobody", CancellationToken.None));
    }
}
