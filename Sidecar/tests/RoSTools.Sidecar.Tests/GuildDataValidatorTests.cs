using RoSTools.Sidecar.Core;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// The regression suite that actually matters. Everything the validator rejects
/// here is something that, if installed, would leave a guildmate with a broken or
/// empty roster and no obvious way to tell why.
/// </summary>
public class GuildDataValidatorTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Accepts_a_real_export()
    {
        var result = GuildDataValidator.Validate(Fixture("valid.lua"));

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(4, result.Entries);
        Assert.Equal("2026-08-23 18:16:47", result.GeneratedAt);
        Assert.Equal(1787509007L, result.GeneratedEpoch);
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
    public void Tolerates_a_schema_2_file_with_no_epoch()
    {
        var content = File.ReadAllText(Fixture("valid.lua"))
            .Replace("    generated_epoch = 1787509007,\n", string.Empty, StringComparison.Ordinal)
            .Replace("    generated_epoch = 1787509007,\r\n", string.Empty, StringComparison.Ordinal);

        var result = GuildDataValidator.ValidateContent(content);

        Assert.True(result.Ok, result.Reason);
        Assert.Null(result.GeneratedEpoch);
        Assert.Equal("2026-08-23 18:16:47", result.GeneratedAt);
    }
}
