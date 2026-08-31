using System.Text;
using RoSTools.Sidecar.Core;
using RoSTools.Sidecar.Core.Blizzard;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// The writer's only real contract is that <see cref="GuildDataValidator"/> accepts
/// what it produces. Everything the validator enforces - the fixed skeleton, key
/// shape, entry syntax, size ceiling - is a guild-wide admission gate now that
/// <c>Core/Sync.lua</c> ships, so a writer that drifts by one character does not
/// produce a slightly-wrong file, it produces a file nobody in the guild can use.
/// These tests therefore run the real validator rather than asserting on strings.
/// </summary>
public class GuildDataWriterTests
{
    private static readonly GuildIdentity Riddle = new("us", "khadgar", "riddle-of-steel");

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static List<RosterEntry> Roster(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new RosterEntry($"Char{i:D3}-khadgar", 250 + (i % 60)))
            .ToList();

    [Fact]
    public void Output_passes_the_real_validator()
    {
        var lua = GuildDataWriter.Render(Roster(40), Riddle, Now, out var dropped);

        var check = GuildDataValidator.ValidateContent(lua, Riddle);

        Assert.True(check.Ok, check.Reason);
        Assert.Equal(40, check.Entries);
        Assert.Empty(dropped);
        Assert.Equal(Riddle, check.Identity);
    }

    /// <summary>
    /// The skeleton check is the one that catches damage outside the tables, and it
    /// compares against a constant. If the writer's header, spacing or trailing
    /// commas drift from the Python exporter's, this is what fails - and it fails
    /// here rather than on a guildmate's client.
    /// </summary>
    [Fact]
    public void Shape_matches_what_the_python_exporter_produces()
    {
        var lua = GuildDataWriter.Render(Roster(3), Riddle, 1787774418, out _);

        Assert.StartsWith("-- RoS-Tools/Data/GuildData.lua\n", lua, StringComparison.Ordinal);
        Assert.Contains("AUTO-GENERATED", lua, StringComparison.Ordinal);
        Assert.Contains("\nlocal _, ns = ...\n", lua, StringComparison.Ordinal);
        Assert.Contains("\nns.GuildData = {\n  meta = {\n", lua, StringComparison.Ordinal);
        Assert.Contains("    generated_epoch = 1787774418,\n", lua, StringComparison.Ordinal);
        Assert.Contains("    generated_at = \"2026-08-26 20:00:18\",\n", lua, StringComparison.Ordinal);
        Assert.Contains("    region = \"us\",\n", lua, StringComparison.Ordinal);
        Assert.Contains("    schema = 3,\n", lua, StringComparison.Ordinal);
        Assert.Contains("  },\n  ilvls = {\n", lua, StringComparison.Ordinal);
        Assert.EndsWith("  },\n}\n", lua, StringComparison.Ordinal);
    }

    /// <summary>
    /// CRLF in the header comment is exactly the damage the skeleton check exists to
    /// catch, and File.WriteAllText on Windows would introduce it silently.
    /// </summary>
    [Fact]
    public void Never_emits_carriage_returns()
    {
        var lua = GuildDataWriter.Render(Roster(5), Riddle, Now, out _);
        Assert.DoesNotContain('\r', lua);
    }

    [Fact]
    public void Writes_utf8_without_a_bom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"writer-{Guid.NewGuid():N}.lua");
        try
        {
            var lua = GuildDataWriter.Render(
                [new RosterEntry("Amélie-khadgar", 280), .. Roster(4)], Riddle, Now, out _);

            GuildDataWriter.WriteTo(path, lua);

            var raw = File.ReadAllBytes(path);
            Assert.False(raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "wrote a BOM");
            Assert.True(GuildDataValidator.Validate(path, Riddle).Ok);
            Assert.Contains("Amélie-khadgar", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// One unusable name must cost that name, not the roster. The validator rejects
    /// the whole file over a single bad key, so the writer has to drop rather than
    /// emit - the NBSP-in-a-name case from the sync audit, generalised.
    /// </summary>
    [Theory]
    [InlineData("NoRealmSuffix")]
    [InlineData("Has Space-khadgar")]
    [InlineData("Colon:Name-khadgar")]
    [InlineData("Pipe|Name-khadgar")]
    [InlineData("Semi;Name-khadgar")]
    [InlineData("Quote\"Name-khadgar")]
    [InlineData("Back\\slash-khadgar")]
    [InlineData("WayTooLongAName" + "abcdefghijklmnopqrstuvwxyz0123456789-khadgar")]
    public void Drops_keys_the_guild_could_not_carry(string bad)
    {
        var entries = new List<RosterEntry>(Roster(6)) { new(bad, 300) };

        var lua = GuildDataWriter.Render(entries, Riddle, Now, out var dropped);

        Assert.Equal([bad], dropped);
        Assert.DoesNotContain(bad, lua, StringComparison.Ordinal);

        var check = GuildDataValidator.ValidateContent(lua, Riddle);
        Assert.True(check.Ok, check.Reason);
        Assert.Equal(6, check.Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10000)]
    public void Drops_out_of_range_item_levels(int ilvl)
    {
        var entries = new List<RosterEntry>(Roster(6)) { new("Odd-khadgar", ilvl) };

        var lua = GuildDataWriter.Render(entries, Riddle, Now, out var dropped);

        Assert.Equal(["Odd-khadgar"], dropped);
        Assert.True(GuildDataValidator.ValidateContent(lua, Riddle).Ok);
    }

    /// <summary>
    /// A duplicate key is a hard validator failure. Connected realms can list the
    /// same character twice, so collapsing beats losing the file.
    /// </summary>
    [Fact]
    public void Collapses_duplicate_keys_keeping_the_higher_item_level()
    {
        var lua = GuildDataWriter.Render(
            [new RosterEntry("Twin-khadgar", 280), new RosterEntry("Twin-khadgar", 315), .. Roster(3)],
            Riddle, Now, out var dropped);

        var check = GuildDataValidator.ValidateContent(lua, Riddle);

        Assert.True(check.Ok, check.Reason);
        Assert.Equal(4, check.Entries);
        Assert.Empty(dropped);
        Assert.Contains("[\"Twin-khadgar\"] = 315,", lua, StringComparison.Ordinal);
        Assert.DoesNotContain("= 280,", lua, StringComparison.Ordinal);
    }

    [Fact]
    public void Sorts_case_insensitively_like_the_python_exporter()
    {
        var lua = GuildDataWriter.Render(
            [
                new RosterEntry("zeta-khadgar", 300),
                new RosterEntry("Alpha-khadgar", 300),
                new RosterEntry("mid-khadgar", 300),
            ],
            Riddle, Now, out _);

        var order = new[] { "Alpha", "mid", "zeta" }
            .Select(n => lua.IndexOf($"[\"{n}", StringComparison.Ordinal))
            .ToList();

        Assert.All(order, i => Assert.True(i > 0));
        Assert.Equal(order.Order().ToList(), order);
    }

    /// <summary>
    /// Over Sync's transfer ceiling the file installs and works locally but can never
    /// be shared, and no peer says why. The validator catches it; this proves the
    /// writer does not sneak past it.
    /// </summary>
    [Fact]
    public void A_roster_too_large_to_share_is_refused_by_the_validator()
    {
        var lua = GuildDataWriter.Render(Roster(2000), Riddle, Now, out _);

        var check = GuildDataValidator.ValidateContent(lua, Riddle);

        Assert.False(check.Ok);
        Assert.Contains("too large to share", check.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_at_is_the_epoch_rendered_as_utc()
    {
        var epoch = 1787774418L;
        var lua = GuildDataWriter.Render(Roster(2), Riddle, epoch, out _);

        var check = GuildDataValidator.ValidateContent(lua, Riddle);

        Assert.Equal(epoch, check.GeneratedEpoch);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            check.GeneratedAt);
    }

    /// <summary>
    /// Identity is compared byte-exactly by Core/Sync.lua, so a pull for a guild this
    /// machine does not carry must never validate against it.
    /// </summary>
    [Fact]
    public void A_different_guild_is_refused_against_a_carried_identity()
    {
        var lua = GuildDataWriter.Render(
            Roster(5), new GuildIdentity("us", "khadgar", "some-other-guild"), Now, out _);

        Assert.False(GuildDataValidator.ValidateContent(lua, Riddle).Ok);
    }
}
