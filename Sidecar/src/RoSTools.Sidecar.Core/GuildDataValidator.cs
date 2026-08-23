using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RoSTools.Sidecar.Core;

public sealed record GuildDataValidation(
    bool Ok,
    string? Reason,
    int Entries,
    string? GeneratedAt,
    long? GeneratedEpoch)
{
    public static GuildDataValidation Fail(string reason) => new(false, reason, 0, null, null);
}

/// <summary>
/// Decides whether a freshly downloaded file is allowed to replace a working
/// <c>Data/GuildData.lua</c>.
/// <para>
/// This is the safety property of the whole sidecar. GitHub serves an HTML error
/// page for a bad path or a private repo, and a dropped connection yields a
/// half-written file; either would silently wipe the roster if installed.
/// </para>
/// <para>
/// <b>This logic is duplicated in three other places on purpose</b> -
/// <c>Tools\Update-RoSTools.ps1</c>, <c>scripts\Install-RoSTools.ps1</c> and
/// <c>scripts\Update-RoSToolsData.ps1</c>. The two <c>scripts\</c> copies must
/// stay self-contained because they are piped straight into <c>iex</c> and cannot
/// dot-source anything. A bug found here must be fixed in all four.
/// </para>
/// </summary>
public static partial class GuildDataValidator
{
    /// <summary>Anything smaller than this is a truncated download, not a roster.</summary>
    private const int MinimumBytes = 200;

    public static GuildDataValidation Validate(string path)
    {
        if (!File.Exists(path))
        {
            return GuildDataValidation.Fail("file was never written");
        }

        var bytes = new FileInfo(path).Length;
        if (bytes < MinimumBytes)
        {
            return GuildDataValidation.Fail($"only {bytes} bytes -- truncated or empty");
        }

        string content;
        try
        {
            content = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return GuildDataValidation.Fail($"could not be read: {ex.Message}");
        }

        return ValidateContent(content, bytes);
    }

    /// <param name="byteCount">
    /// Byte length of the payload. Passed separately because the size floor is a
    /// check on what came off the wire, not on the decoded character count.
    /// </param>
    public static GuildDataValidation ValidateContent(string content, long? byteCount = null)
    {
        var size = byteCount ?? Encoding.UTF8.GetByteCount(content);
        if (size < MinimumBytes)
        {
            return GuildDataValidation.Fail($"only {size} bytes -- truncated or empty");
        }

        if (LeadingAngleBracket().IsMatch(content))
        {
            return GuildDataValidation.Fail(
                "server returned HTML, not Lua (check the URL and that the repo is public)");
        }

        if (!content.Contains("AUTO-GENERATED", StringComparison.Ordinal))
        {
            return GuildDataValidation.Fail("missing the generated-file header");
        }

        if (!GuildDataAssignment().IsMatch(content))
        {
            return GuildDataValidation.Fail("no ns.GuildData assignment");
        }

        if (!IlvlsTable().IsMatch(content))
        {
            return GuildDataValidation.Fail("no ilvls table");
        }

        // Balanced-enough check: a truncated file loses its closing braces.
        var open = content.Count(c => c == '{');
        var close = content.Count(c => c == '}');
        if (open != close)
        {
            return GuildDataValidation.Fail(
                $"unbalanced braces ({open} open, {close} close) -- truncated");
        }

        var entries = CharacterEntry().Count(content);
        if (entries < 1)
        {
            return GuildDataValidation.Fail("no character entries found");
        }

        string? generatedAt = null;
        var stamp = GeneratedAt().Match(content);
        if (stamp.Success)
        {
            generatedAt = stamp.Groups[1].Value;
        }

        long? generatedEpoch = null;
        var epoch = GeneratedEpoch().Match(content);
        if (epoch.Success &&
            long.TryParse(epoch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            generatedEpoch = parsed;
        }

        return new GuildDataValidation(true, null, entries, generatedAt, generatedEpoch);
    }

    [GeneratedRegex(@"^\s*<")]
    private static partial Regex LeadingAngleBracket();

    [GeneratedRegex(@"ns\.GuildData\s*=")]
    private static partial Regex GuildDataAssignment();

    [GeneratedRegex(@"ilvls\s*=")]
    private static partial Regex IlvlsTable();

    [GeneratedRegex(@"\[""[^""]+""\]\s*=\s*\d+")]
    private static partial Regex CharacterEntry();

    [GeneratedRegex(@"generated_at\s*=\s*""([^""]+)""")]
    private static partial Regex GeneratedAt();

    [GeneratedRegex(@"generated_epoch\s*=\s*(\d+)")]
    private static partial Regex GeneratedEpoch();
}
