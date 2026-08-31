using System.Text;
using RoSTools.Sidecar.Core;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// The regression suite that actually matters. Everything the validator rejects
/// here is something that, if installed, would leave a guildmate with a broken or
/// empty roster and no obvious way to tell why.
/// <para>
/// Since Core/Sync.lua exists, most of these are guild-wide rather than local: a
/// file this validator accepts is a file every client in the guild will be offered
/// and will try to adopt. The cases below are grouped by which of Sync's own rules
/// they mirror.
/// </para>
/// </summary>
public class GuildDataValidatorTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string Valid => File.ReadAllText(Fixture("valid.lua"));

    /// <summary>Swap one literal in the fixture, for the "one thing wrong" cases below.</summary>
    private static GuildDataValidation WithEdit(string find, string replace, GuildIdentity? expected = null) =>
        GuildDataValidator.ValidateContent(
            Valid.Replace(find, replace, StringComparison.Ordinal), expected);

    [Fact]
    public void Accepts_a_real_export()
    {
        var result = GuildDataValidator.Validate(Fixture("valid.lua"));

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(4, result.Entries);
        Assert.Equal("2026-08-23 18:16:47", result.GeneratedAt);
        Assert.Equal(1787509007L, result.GeneratedEpoch);
        Assert.Equal("us/khadgar/riddle-of-steel", result.Identity?.ToString());
    }

    [Fact]
    public void Accepts_the_roster_this_repo_actually_ships()
    {
        // The fixture is four hand-written entries. This is the real export, with
        // real non-ASCII names and real realm slugs, and it has to survive every
        // rule below or the sidecar refuses the only file it will ever be given.
        var result = GuildDataValidator.Validate(Fixture("shipped-GuildData.lua"));

        Assert.True(result.Ok, result.Reason);
        Assert.True(result.Entries > 100, $"only {result.Entries} entries parsed");
        Assert.InRange(result.ExportBytes, 1, 40000);
    }

    // ------------------------------------------------------------------
    // Guild identity. Everything here is about one client poisoning the guild.
    // ------------------------------------------------------------------

    [Fact]
    public void Refuses_a_roster_for_a_different_guild()
    {
        var result = WithEdit(
            "guild = \"riddle-of-steel\"",
            "guild = \"some-other-guild\"",
            new GuildIdentity("us", "khadgar", "riddle-of-steel"));

        Assert.False(result.Ok);
        Assert.Contains("some-other-guild", result.Reason);
    }

    [Fact]
    public void Refuses_the_expected_guild_when_only_the_case_differs()
    {
        // Byte-exact on purpose. Sync.lua compares identity with a plain Lua string
        // comparison, so "US/khadgar/..." and "us/khadgar/..." are different guilds
        // on the wire. Accepting the recapitalised file here would install it, and
        // that client would then hold the highest epoch in the guild while every
        // peer rejected its snapshot on identity - the exact failure this check
        // exists to prevent, reintroduced by being lenient about case.
        var result = GuildDataValidator.ValidateContent(
            Valid, new GuildIdentity("US", "Khadgar", "Riddle-Of-Steel"));

        Assert.False(result.Ok);
        Assert.Contains("Check the data source URL", result.Reason);
    }

    [Fact]
    public void Refuses_a_file_with_no_guild_in_its_meta()
    {
        var result = WithEdit("guild = \"riddle-of-steel\",", string.Empty);

        Assert.False(result.Ok);
        Assert.Contains("region/realm/guild", result.Reason);
    }

    [Theory]
    [InlineData("riddle:of:steel")]
    [InlineData("riddle;of;steel")]
    [InlineData("riddle|of|steel")]
    public void Refuses_meta_that_would_break_the_sharing_header(string guild)
    {
        // These three fields go verbatim into Sync.lua's
        // "H:epoch:region:realm:guild:schema" header, where ':' and ';' are the
        // separators. One of them here and every peer fails to parse every snapshot
        // this client ever serves. '|' is chat markup wherever the name is printed.
        var result = WithEdit("riddle-of-steel", guild);

        Assert.False(result.Ok);
        Assert.Contains("header", result.Reason);
    }

    [Fact]
    public void Allows_meta_characters_the_header_can_actually_carry()
    {
        // Sync's header captures are "[^:;]*", so a space is fine. Refusing one was
        // an over-strict mirror that would have rejected a legitimate export if the
        // exporter ever emitted display names instead of slugs.
        var result = WithEdit("riddle-of-steel", "riddle of steel");

        Assert.True(result.Ok, result.Reason);
    }

    // ------------------------------------------------------------------
    // Epoch. Sync orders the whole guild by this one number.
    // ------------------------------------------------------------------

    [Fact]
    public void Refuses_a_future_dated_export()
    {
        // Unbeatable by any real export and unchaseable by any peer: the client is
        // frozen on it permanently while reporting itself perfectly up to date.
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var result = WithEdit("1787509007", future.ToString());

        Assert.False(result.Ok);
        Assert.Contains("future", result.Reason);
    }

    [Fact]
    public void Tolerates_clock_skew_inside_the_sync_window()
    {
        var soon = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds();
        var result = WithEdit("1787509007", soon.ToString());

        Assert.True(result.Ok, result.Reason);
    }

    [Fact]
    public void Refuses_a_file_with_no_epoch()
    {
        // Was tolerated when the file was only read locally. With sync, epoch 0 means
        // the client never announces, never serves and never adopts - it drops out of
        // the guild's roster sharing entirely, and nothing says so.
        var result = WithEdit("generated_epoch = 1787509007,", string.Empty);

        Assert.False(result.Ok);
        Assert.Contains("generated_epoch", result.Reason);
    }

    // ------------------------------------------------------------------
    // Entries. Sync re-checks every one of these on every receiving client.
    // ------------------------------------------------------------------

    [Fact]
    public void Refuses_lua_that_is_brace_balanced_but_will_not_load()
    {
        // Brace counting cannot see a missing comma. WoW's parser can: it skips the
        // file, ns.GuildData is nil, and the client shows zero characters and can
        // never adopt a snapshot again, because the identity check reads that table.
        var result = WithEdit(
            "[\"Icebyte-moon-guard\"] = 302,",
            "[\"Icebyte-moon-guard\"] = 302");

        Assert.False(result.Ok);
        Assert.Contains("missing comma", result.Reason);
    }

    [Fact]
    public void Refuses_junk_inside_the_ilvls_table()
    {
        var result = WithEdit(
            "[\"Icebyte-moon-guard\"] = 302,",
            "[\"Icebyte-moon-guard\"] = 302, oops,");

        Assert.False(result.Ok);
        Assert.Contains("unexpected content", result.Reason);
    }

    [Fact]
    public void Counts_only_entries_inside_the_ilvls_table()
    {
        // The old count was a regex over the whole file, so a bracket-quoted number
        // anywhere else made an empty roster look populated - including one sitting
        // in a comment, as here.
        var result = WithEdit(
            "local _, ns = ...",
            "-- e.g. [\"Decoy-realm\"] = 1\nlocal _, ns = ...");

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(4, result.Entries);
    }

    [Fact]
    public void Refuses_a_third_table_beside_meta_and_ilvls()
    {
        var result = WithEdit("  ilvls = {", "  other = { [\"Decoy-realm\"] = 1 },\n  ilvls = {");

        Assert.False(result.Ok);
        Assert.Contains("not shaped like a generated GuildData.lua", result.Reason);
    }

    [Fact]
    public void Refuses_stray_code_outside_the_tables()
    {
        // The whole class of damage the block parsing cannot see: braces balance,
        // both tables are intact and parse cleanly, and Lua still refuses the file.
        // A line break landing inside the generated header comment does exactly this.
        var result = WithEdit(
            "-- AUTO-GENERATED by Tools/fetch_guild_info.py",
            "-- AUTO-GENERATED by Tools/f\netch_guild_info.py");

        Assert.False(result.Ok);
        Assert.Contains("outside the meta and ilvls tables", result.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1000000)]
    public void Refuses_out_of_range_item_levels(int ilvl)
    {
        // Sync drops these per-entry and rejects the whole snapshot past 10% of them.
        // Under that line is the worse half: those members are silently deleted from
        // every other client's roster while this one still shows them.
        var result = WithEdit("] = 302,", $"] = {ilvl},");

        Assert.False(result.Ok);
        Assert.Contains("out-of-range", result.Reason);
    }

    [Theory]
    [InlineData("NoRealm")]
    [InlineData("Has Space-khadgar")]
    [InlineData("Semi;colon-khadgar")]
    [InlineData("Equals=sign-khadgar")]
    [InlineData("|cffff0000Red|r-khadgar")]
    [InlineData("Waaaaaaytoolongakeyfortheprotocoltocarryatallreally-khadgar")]
    public void Refuses_keys_sync_would_drop(string key)
    {
        // The markup case is the one that lands locally rather than guild-wide: keys
        // are printed straight into the chat frame and the roster browser, so a
        // colour escape in one is live markup, and Sync's own filter never sees a
        // file this client installed itself.
        var result = WithEdit("Icebyte-moon-guard", key);

        Assert.False(result.Ok);
        Assert.Contains("not a usable character key", result.Reason);
    }

    [Fact]
    public void Accepts_non_ascii_character_names()
    {
        // Real members. A locale-dependent letter class would drop them silently.
        var result = WithEdit("Icebyte-moon-guard", "Arrøw-antonidas");

        Assert.True(result.Ok, result.Reason);
    }

    [Fact]
    public void Refuses_duplicate_keys()
    {
        var result = WithEdit("[\"Bonecholos-zuljin\"] = 658,", "[\"Icebyte-moon-guard\"] = 658,");

        Assert.False(result.Ok);
        Assert.Contains("duplicate", result.Reason);
    }

    [Fact]
    public void Refuses_a_roster_too_large_to_share()
    {
        // Installs and works locally, but every peer's request is refused and nobody
        // finds out why. The ceiling is Sync's MAX_CHUNKS * CHUNK_SIZE.
        var padding = string.Join("\n", Enumerable.Range(0, 2500)
            .Select(i => $"    [\"Filler{i}-khadgar\"] = 300,"));

        var result = GuildDataValidator.ValidateContent(
            Valid.Replace("  ilvls = {", "  ilvls = {\n" + padding, StringComparison.Ordinal));

        Assert.False(result.Ok);
        Assert.Contains("too large to share", result.Reason);
    }

    [Fact]
    public void Reports_the_export_size_a_peer_transfer_would_carry()
    {
        var result = GuildDataValidator.Validate(Fixture("valid.lua"));

        // "H:1787509007:us:khadgar:riddle-of-steel:3;" is 42 bytes, then "key=ilvl;"
        // for each of the four entries: key length + 2 separators + 3 digits.
        Assert.Equal(42 + 23 + 22 + 21 + 27, result.ExportBytes);
    }

    [Fact]
    public void Reads_the_identity_of_an_installed_file()
    {
        Assert.Equal(
            "us/khadgar/riddle-of-steel",
            GuildDataValidator.IdentityOf(Fixture("valid.lua"))?.ToString());
    }

    // ------------------------------------------------------------------
    // Lua the scanner has to understand. Every case here is one where a
    // cheaper scanner was wrong in one direction or the other.
    // ------------------------------------------------------------------

    [Fact]
    public void Refuses_an_unterminated_long_comment()
    {
        // Brace-balanced, and the old scanner reported four healthy entries. Lua
        // says "unfinished long comment near '<eof>'" and skips the whole file.
        var result = WithEdit("  ilvls = {", "  ilvls = {\n    --[[ oops");

        Assert.False(result.Ok);
        Assert.Contains("--[[", result.Reason);
    }

    [Fact]
    public void Accepts_a_well_formed_long_comment()
    {
        var result = WithEdit(
            "    [\"Crackilz-area-52\"] = 289,",
            "    --[[ these two were\n         checked by hand ]]\n    [\"Crackilz-area-52\"] = 289,");

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(4, result.Entries);
    }

    [Fact]
    public void Accepts_a_quote_inside_a_comment()
    {
        // A scanner that tracked strings but not comments treated this quote as
        // opening a string, ate the table's closing brace, and reported "no meta
        // table" for a file with a perfectly good meta table.
        var result = WithEdit(
            "  meta = {",
            "  meta = {\n    -- the exporter\"s clock is UTC");

        Assert.True(result.Ok, result.Reason);
    }

    [Fact]
    public void Accepts_braces_inside_quoted_text()
    {
        // The old whole-file brace counter was string-unaware and rejected these as
        // "truncated", which is both wrong and a misleading thing to tell someone.
        var result = WithEdit("2026-08-23 18:16:47", "2026-08-23 18:16:47 }");

        Assert.True(result.Ok, result.Reason);
    }

    [Fact]
    public void Refuses_a_newline_inside_a_key()
    {
        // .NET's '$' matches before a trailing newline and '[^"]' matches one, so a
        // key ending in a line break passed both the entry regex and the key-shape
        // check. The resulting file is not compilable Lua at all.
        var result = WithEdit("[\"Toon-khadgar\"]", "[\"Toon-khadgar\n\"]")
            .Ok
            ? WithEdit("[\"Icebyte-moon-guard\"]", "[\"Icebyte-moon-guard\n\"]")
            : WithEdit("[\"Icebyte-moon-guard\"]", "[\"Icebyte-moon-guard\n\"]");

        Assert.False(result.Ok);
    }

    [Fact]
    public void Refuses_a_meta_field_assigned_twice()
    {
        // A Lua table keeps the LAST assignment; a first-match regex read the first.
        // That gap defeated both the guild check and the future-epoch check at once.
        var result = WithEdit(
            "    guild = \"riddle-of-steel\",",
            "    guild = \"riddle-of-steel\",\n    guild = \"some-other-guild\",");

        Assert.False(result.Ok);
        Assert.Contains("twice", result.Reason);
    }

    [Fact]
    public void Refuses_a_second_ilvls_table()
    {
        var result = WithEdit(
            "  ilvls = {",
            "  ilvls = { [\"Decoy-khadgar\"] = 1 },\n  ilvls = {");

        Assert.False(result.Ok);
        Assert.Contains("two ilvls tables", result.Reason);
    }

    [Fact]
    public void Refuses_a_utf16_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".lua");
        File.WriteAllText(path, Valid, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        try
        {
            var result = GuildDataValidator.Validate(path);

            Assert.False(result.Ok);
            Assert.Contains("UTF-16", result.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Refuses_more_characters_than_a_guildmate_will_accept()
    {
        // Sync's MAX_ENTRIES. Short keys keep this under the byte ceiling, so the
        // size check alone would have let it through to be transferred and rejected.
        var padding = string.Join("\n", Enumerable.Range(0, 5001)
            .Select(i => $"    [\"F{i}-kh\"] = 300,"));

        var result = GuildDataValidator.ValidateContent(
            Valid.Replace("  ilvls = {", "  ilvls = {\n" + padding, StringComparison.Ordinal));

        Assert.False(result.Ok);
        Assert.Contains("5000", result.Reason);
    }

    [Fact]
    public void Warns_but_still_installs_an_export_too_old_to_share()
    {
        // Past Sync's MAX_AGE no peer will accept this roster, so sharing has
        // stopped guild-wide. Still the best data available locally, though, so it
        // installs - with something to say, which is the part that was missing.
        var old = DateTimeOffset.UtcNow.AddDays(-100).ToUnixTimeSeconds();
        var result = WithEdit("1787509007", old.ToString());

        Assert.True(result.Ok, result.Reason);
        Assert.Contains("roster sharing has stopped", result.Warning);
    }

    [Fact]
    public void Reports_no_installed_stamp_for_a_truncated_file()
    {
        // This is what makes the sidecar refetch instead of trusting its ETag - and
        // it has to be the full validation, not just an epoch read. meta comes first
        // in the file, so this fixture (truncated inside ilvls) has a perfectly
        // readable generated_epoch; matching on that alone would have the sidecar
        // answer 304 over a file the addon can no longer load.
        Assert.Null(GuildDataValidator.InstalledStamp(Fixture("truncated.lua")));
    }

    [Fact]
    public void Reports_an_installed_stamp_for_an_intact_file()
    {
        Assert.Equal(1787509007L, GuildDataValidator.InstalledStamp(Fixture("valid.lua")));
    }

    [Fact]
    public void Rejects_the_github_error_page()
    {
        // The failure this whole class exists for: a bad path or a repo flipped
        // private returns HTML with a 200-shaped body from some proxies.
        var result = GuildDataValidator.Validate(Fixture("github-404.html"));

        Assert.False(result.Ok);
        Assert.Contains("HTML", result.Reason);
    }

    [Fact]
    public void Rejects_a_truncated_download()
    {
        var result = GuildDataValidator.Validate(Fixture("truncated.lua"));

        Assert.False(result.Ok);
        Assert.Contains("truncated", result.Reason);
    }

    [Fact]
    public void Rejects_unbalanced_braces()
    {
        var result = GuildDataValidator.Validate(Fixture("unbalanced.lua"));

        Assert.False(result.Ok);
        Assert.Contains("unbalanced braces", result.Reason);
    }

    [Fact]
    public void Rejects_an_empty_file()
    {
        // Written here rather than shipped as a fixture: a zero-byte file in the
        // repo is the kind of thing a checkout or a packaging step quietly eats.
        var empty = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".lua");
        File.WriteAllText(empty, string.Empty);

        try
        {
            var result = GuildDataValidator.Validate(empty);

            Assert.False(result.Ok);
            Assert.Contains("truncated or empty", result.Reason);
        }
        finally
        {
            File.Delete(empty);
        }
    }

    [Fact]
    public void Rejects_a_well_formed_file_with_no_characters()
    {
        var result = GuildDataValidator.Validate(Fixture("no-entries.lua"));

        Assert.False(result.Ok);
        Assert.Contains("no character entries", result.Reason);
    }

    [Fact]
    public void Rejects_a_missing_file()
    {
        var result = GuildDataValidator.Validate(Fixture("does-not-exist.lua"));

        Assert.False(result.Ok);
        Assert.Contains("never written", result.Reason);
    }

    [Fact]
    public void Rejects_lua_without_the_generated_header()
    {
        var content = File.ReadAllText(Fixture("valid.lua"))
            .Replace("AUTO-GENERATED", "hand written", StringComparison.Ordinal);

        var result = GuildDataValidator.ValidateContent(content);

        Assert.False(result.Ok);
        Assert.Contains("generated-file header", result.Reason);
    }

    [Fact]
    public void Rejects_lua_without_the_GuildData_assignment()
    {
        var content = File.ReadAllText(Fixture("valid.lua"))
            .Replace("ns.GuildData =", "ns.SomethingElse =", StringComparison.Ordinal);

        var result = GuildDataValidator.ValidateContent(content);

        Assert.False(result.Ok);
        Assert.Contains("ns.GuildData", result.Reason);
    }

    [Fact]
    public void Rejects_lua_without_an_ilvls_table()
    {
        var content = File.ReadAllText(Fixture("valid.lua"))
            .Replace("ilvls =", "levels =", StringComparison.Ordinal);

        var result = GuildDataValidator.ValidateContent(content);

        Assert.False(result.Ok);
        Assert.Contains("ilvls table", result.Reason);
    }

    [Fact]
    public void No_longer_tolerates_a_schema_2_file_with_no_epoch()
    {
        // A deliberate tightening. This used to be accepted, because a file with only
        // generated_at still shows correct tooltips locally. It cannot stay accepted:
        // Data:GeneratedEpoch() returns nil, so myEpoch() is 0, so the client never
        // announces, never serves and never adopts. It vanishes from guild roster
        // sharing with no error anywhere. Failing the install is louder and the
        // exporter has emitted schema 3 since well before the sidecar existed.
        var content = File.ReadAllText(Fixture("valid.lua"))
            .Replace("    generated_epoch = 1787509007,\n", string.Empty, StringComparison.Ordinal)
            .Replace("    generated_epoch = 1787509007,\r\n", string.Empty, StringComparison.Ordinal);

        var result = GuildDataValidator.ValidateContent(content);

        Assert.False(result.Ok);
        Assert.Contains("generated_epoch", result.Reason);
    }
}
