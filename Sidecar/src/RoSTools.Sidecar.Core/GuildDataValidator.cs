using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RoSTools.Sidecar.Core;

/// <summary>The guild a roster file claims to describe.</summary>
public sealed record GuildIdentity(string Region, string Realm, string Guild)
{
    /// <summary>
    /// Byte-exact, deliberately. <c>Core/Sync.lua</c> compares identity with a plain
    /// Lua string comparison (<c>id ~= theirs</c>), so "US" and "us" are different
    /// guilds out on the wire. Matching case-insensitively here would let a file with
    /// the region capitalised install, and that client would then announce the
    /// highest epoch in the guild while every peer rejected its snapshot on identity.
    /// </summary>
    public bool Matches(GuildIdentity other) =>
        string.Equals(Region, other.Region, StringComparison.Ordinal) &&
        string.Equals(Realm, other.Realm, StringComparison.Ordinal) &&
        string.Equals(Guild, other.Guild, StringComparison.Ordinal);

    public override string ToString() => $"{Region}/{Realm}/{Guild}";
}

public sealed record GuildDataValidation(
    bool Ok,
    string? Reason,
    int Entries,
    string? GeneratedAt,
    long? GeneratedEpoch,
    GuildIdentity? Identity = null,
    int ExportBytes = 0,
    string? Warning = null)
{
    public static GuildDataValidation Fail(string reason) => new(false, reason, 0, null, null);

    /// <summary>Days between the export instant and now, or null when there is no epoch.</summary>
    public double? AgeInDays
    {
        get
        {
            if (GeneratedEpoch is not { } epoch)
            {
                return null;
            }

            try
            {
                return (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(epoch)).TotalDays;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// Decides whether a freshly downloaded file is allowed to replace a working
/// <c>Data/GuildData.lua</c>.
/// <para>
/// This is the safety property of the whole sidecar, and since <c>Core/Sync.lua</c>
/// exists it is a <b>guild-wide</b> one. A client that installs a file does not just
/// read it: it announces that file's <c>generated_epoch</c> to the guild and serves
/// the contents to every peer that asks. So a file this method accepts is a file the
/// whole guild will try to adopt, and anything Sync.lua would refuse must be refused
/// here instead - on one machine, loudly - rather than out there, silently, on
/// everyone's.
/// </para>
/// <para>
/// GitHub also serves an HTML error page for a bad path or a private repo, and a
/// dropped connection yields a half-written file; either would wipe the roster.
/// </para>
/// <para>
/// <b>This is now the only copy of these rules outside the addon.</b> Three
/// PowerShell duplicates used to exist in <c>Tools\</c> and <c>scripts\</c>; those
/// scripts were deleted on 2026-08-29 when CurseForge plus <c>Core/Sync.lua</c>
/// became the general-user path, so a fix here no longer has to be mirrored
/// anywhere. What it does still have to stay in step with is what
/// <c>Core/Sync.lua</c> accepts from a peer.
/// </para>
/// <para>
/// That matters more than it looks. Whatever this machine installs, it announces
/// to the guild and serves to every peer that asks - so this is a <b>guild-wide
/// admission gate</b>, not a local safety net. A file this validator waves
/// through and Sync then rejects is a file that stalls every stale client in the
/// guild.
/// </para>
/// </summary>
public static partial class GuildDataValidator
{
    /// <summary>Anything smaller than this is a truncated download, not a roster.</summary>
    private const int MinimumBytes = 200;

    // ------------------------------------------------------------------
    // Limits mirrored from Core/Sync.lua. Changing one of these without
    // changing the other end turns a local reject into a silent guild-wide
    // one, which is the failure mode this class exists to prevent.
    // ------------------------------------------------------------------

    /// <summary>Sync.lua's <c>MAX_KEY_LEN</c>. Bytes, not characters - it is <c>#key</c> in Lua.</summary>
    private const int MaxKeyBytes = 48;

    /// <summary>Sync.lua's per-entry item level bounds.</summary>
    private const int MinIlvl = 1;
    private const int MaxIlvl = 9999;

    /// <summary>Sync.lua's <c>MAX_ENTRIES</c>: the receiver's absolute count ceiling.</summary>
    private const int MaxEntries = 5000;

    /// <summary>Sync.lua's <c>MAX_CHUNKS * CHUNK_SIZE</c>: what one transfer can carry.</summary>
    private const int MaxExportBytes = 200 * 200;

    /// <summary>Sync.lua's <c>MAX_FUTURE</c>: tolerance for clock skew against the exporter.</summary>
    private const int MaxFutureSeconds = 300;

    /// <summary>
    /// Sync.lua's <c>MAX_AGE</c>. Not a rejection - an export this old is still the
    /// best data available locally - but past it no client in the guild will accept
    /// it from a peer, so roster sharing has quietly stopped and somebody should know.
    /// </summary>
    private const long MaxAgeSeconds = 90L * 86400L;

    /// <param name="expected">
    /// The guild this machine is supposed to be carrying. When supplied, a file
    /// describing any other guild is refused. Without this check a mistyped data URL
    /// installs another guild's roster, and that client then holds the highest epoch
    /// in the guild forever: every peer picks it, transfers the whole snapshot, and
    /// rejects it on identity - silently, every anti-entropy window.
    /// </param>
    public static GuildDataValidation Validate(string path, GuildIdentity? expected = null)
    {
        if (!File.Exists(path))
        {
            return GuildDataValidation.Fail("file was never written");
        }

        long bytes;
        byte[] raw;
        try
        {
            bytes = new FileInfo(path).Length;
            raw = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return GuildDataValidation.Fail($"could not be read: {ex.Message}");
        }

        if (bytes < MinimumBytes)
        {
            return GuildDataValidation.Fail($"only {bytes} bytes -- truncated or empty");
        }

        // WoW reads addon Lua as bytes; a UTF-16 file is accepted by .NET's decoder
        // (it honours the BOM) but is not something Lua can parse at all.
        if (raw.Length >= 2 &&
            ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF)))
        {
            return GuildDataValidation.Fail("file is UTF-16; the addon can only read UTF-8");
        }

        return ValidateContent(Encoding.UTF8.GetString(raw).TrimStart('﻿'), expected, bytes);
    }

    /// <summary>The guild an already-installed file claims, or null if it cannot be read.</summary>
    public static GuildIdentity? IdentityOf(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var content = File.ReadAllText(path, Encoding.UTF8);
            var blocks = ScanBlocks(content);
            return blocks.Meta is null ? null : ReadIdentity(blocks.Meta, out _);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The <c>generated_epoch</c> of an intact roster already on disk, or null if
    /// there isn't one.
    /// <para>
    /// This is what tells the sidecar whether the file at a destination is still the
    /// one it installed, so it has to answer "is this a good file that is also that
    /// file", not merely "can I find an epoch in it". Reading the epoch alone was not
    /// enough: <c>meta</c> comes first in the file, so a download truncated inside
    /// <c>ilvls</c> still yields the original stamp, and the sidecar would go on
    /// answering 304 over a roster the addon can no longer load.
    /// </para>
    /// </summary>
    public static long? InstalledStamp(string path)
    {
        var check = Validate(path);
        return check.Ok ? check.GeneratedEpoch : null;
    }

    /// <summary>
    /// The character entries of a roster file, or null when it cannot be parsed.
    /// <para>
    /// Read-only and additive: this exists so the web console can diff a fresh pull
    /// against what is already installed before offering to replace it. It runs the
    /// same strict parse <see cref="ValidateContent"/> does and shares its rejection
    /// rules, so it can never report entries the validator would refuse.
    /// </para>
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, long>>? EntriesOf(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var content = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
            var blocks = ScanBlocks(content);
            if (blocks.Ilvls is null)
            {
                return null;
            }

            var parsed = ParseEntries(blocks.Ilvls.Body, out _);
            return parsed?.Select(e => new KeyValuePair<string, long>(e.Key, e.Ilvl)).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read entries from {path}: {ex.Message}");
            return null;
        }
    }

    /// <param name="byteCount">
    /// Byte length of the payload. Passed separately because the size floor is a
    /// check on what came off the wire, not on the decoded character count.
    /// </param>
    public static GuildDataValidation ValidateContent(
        string content,
        GuildIdentity? expected = null,
        long? byteCount = null)
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

        // Ordinal and case-sensitive on purpose - see the class remarks.
        if (!content.Contains("AUTO-GENERATED", StringComparison.Ordinal))
        {
            return GuildDataValidation.Fail("missing the generated-file header");
        }

        if (!GuildDataAssignment().IsMatch(content))
        {
            return GuildDataValidation.Fail("no ns.GuildData assignment");
        }

        var blocks = ScanBlocks(content);
        if (blocks.Error is not null)
        {
            return GuildDataValidation.Fail(blocks.Error);
        }

        if (blocks.Meta is null)
        {
            return GuildDataValidation.Fail("no meta table");
        }

        if (blocks.Ilvls is null)
        {
            return GuildDataValidation.Fail("no ilvls table");
        }

        // Damage outside the two tables is invisible to everything above: the braces
        // still balance, both tables still parse, and the file is still not Lua. This
        // is a generated file with exactly one shape, so compare against it.
        if (blocks.Skeleton != ExpectedSkeleton)
        {
            return GuildDataValidation.Fail(
                "the file is not shaped like a generated GuildData.lua -- " +
                "there is content outside the meta and ilvls tables");
        }

        // --- identity -------------------------------------------------
        var identity = ReadIdentity(blocks.Meta, out var metaError);
        if (identity is null)
        {
            return GuildDataValidation.Fail(metaError ?? "meta is missing region/realm/guild");
        }

        // These three go into the sync wire header verbatim, where ':' and ';' are
        // the field separators, and '|' is markup wherever an adopted key is printed.
        // Deliberately NOT wider than that: Sync's header captures are "[^:;]*", so
        // refusing a space or an '=' here would reject a legitimate export - one
        // using display names rather than slugs - that the wire would carry fine.
        foreach (var (field, value) in new[]
                 {
                     ("region", identity.Region), ("realm", identity.Realm), ("guild", identity.Guild),
                 })
        {
            if (HeaderUnsafe().IsMatch(value))
            {
                return GuildDataValidation.Fail(
                    $"meta.{field} contains a character the roster-sharing header cannot carry");
            }
        }

        if (expected is not null && !identity.Matches(expected))
        {
            return GuildDataValidation.Fail(
                $"this file is for {identity}, but this machine carries {expected}. " +
                "Check the data source URL.");
        }

        // --- epoch ----------------------------------------------------
        if (!blocks.Meta.Fields.TryGetValue("generated_epoch", out var epochField) ||
            !long.TryParse(epochField, NumberStyles.Integer, CultureInfo.InvariantCulture, out var generatedEpoch) ||
            generatedEpoch < 1)
        {
            // Sync orders every client's data by this one number. A file without it
            // reports epoch 0, so the client never announces and never adopts.
            return GuildDataValidation.Fail("meta has no usable generated_epoch");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (generatedEpoch - now > MaxFutureSeconds)
        {
            // A future epoch is unbeatable: no real export can ever outrank it, and
            // no peer will chase it either (they apply the same bound). The client
            // would be frozen on this file permanently, reporting itself as current.
            return GuildDataValidation.Fail(
                $"generated_epoch is {generatedEpoch - now} seconds in the future -- " +
                "check the exporter's clock");
        }

        string? warning = null;
        if (now - generatedEpoch > MaxAgeSeconds)
        {
            warning = $"this export is {(now - generatedEpoch) / 86400} days old -- past the point " +
                      "where guildmates will accept it, so roster sharing has stopped.";
        }

        blocks.Meta.Fields.TryGetValue("generated_at", out var generatedAt);
        blocks.Meta.Fields.TryGetValue("schema", out var schema);
        schema ??= string.Empty;

        // --- entries --------------------------------------------------
        var parsedEntries = ParseEntries(blocks.Ilvls.Body, out var entryError);
        if (parsedEntries is null)
        {
            return GuildDataValidation.Fail(entryError!);
        }

        if (parsedEntries.Count < 1)
        {
            return GuildDataValidation.Fail("no character entries found");
        }

        if (parsedEntries.Count > MaxEntries)
        {
            return GuildDataValidation.Fail(
                $"roster has {parsedEntries.Count} characters, over the {MaxEntries} a guildmate will accept");
        }

        // --- shareable size -------------------------------------------
        // What Core/Sync.lua's Data:Export() would serialize, to the byte. A roster
        // over the transfer ceiling installs and works locally but can never be
        // shared: every peer's request is refused, and nobody finds out why.
        var exportBytes = Encoding.UTF8.GetByteCount(
            $"H:{generatedEpoch}:{identity.Region}:{identity.Realm}:{identity.Guild}:{schema};");

        foreach (var (key, ilvl) in parsedEntries)
        {
            exportBytes += Encoding.UTF8.GetByteCount(key) + 2 +
                           ilvl.ToString(CultureInfo.InvariantCulture).Length;
        }

        if (exportBytes > MaxExportBytes)
        {
            return GuildDataValidation.Fail(
                $"roster is too large to share ({exportBytes} bytes, limit {MaxExportBytes})");
        }

        return new GuildDataValidation(
            true, null, parsedEntries.Count, generatedAt, generatedEpoch, identity, exportBytes, warning);
    }

    // ------------------------------------------------------------------
    // Scanning
    //
    // One pass over the file that understands Lua strings and both comment
    // forms, because every cheaper approach was wrong in both directions:
    // counting braces with a regex rejected a valid file with a '{' inside a
    // quoted name, and skipping strings but not comments rejected a valid file
    // with a '"' inside a comment while accepting an unterminated long comment
    // that WoW cannot parse at all.
    // ------------------------------------------------------------------

    private sealed record LuaTable(string Body)
    {
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The file with comments removed, strings reduced to <c>S</c>, the contents of
    /// the two inner tables reduced to <c>#</c>, and whitespace collapsed. For any
    /// real export this is exactly <see cref="ExpectedSkeleton"/>.
    /// <para>
    /// This is what catches damage <i>outside</i> the tables, which the block parsing
    /// cannot see at all. A newline landing inside the generated header comment, for
    /// instance, turns the rest of that line into a bare statement: brace-balanced,
    /// both tables intact and fully parseable, and still a file Lua refuses to load -
    /// leaving the addon with no data and no way back. Fuzzing found 14 such files in
    /// 400 before this check existed and none after.
    /// </para>
    /// </summary>
    private const string ExpectedSkeleton = "local _, ns = ... ns.GuildData = { meta = {#}, ilvls = {#}, }";

    private sealed record ScanResult(LuaTable? Meta, LuaTable? Ilvls, string? Error, string Skeleton = "");

    private static ScanResult ScanBlocks(string content)
    {
        LuaTable? meta = null, ilvls = null;
        var skeleton = new StringBuilder();
        var depth = 0;
        var i = 0;

        // Where a table opened, keyed by the depth it opened at, plus the name that
        // introduced it. Only names seen at depth 1 (directly inside ns.GuildData)
        // are of interest.
        var openAt = new Dictionary<int, (string? Name, int Start)>();
        string? pendingName = null;

        while (i < content.Length)
        {
            var c = content[i];

            // --- comments ---
            if (c == '-' && i + 1 < content.Length && content[i + 1] == '-')
            {
                var afterDashes = i + 2;
                var longLevel = LongBracketLevel(content, afterDashes);
                if (longLevel >= 0)
                {
                    var close = FindLongBracketClose(content, afterDashes, longLevel);
                    if (close < 0)
                    {
                        return new ScanResult(null, null, "unterminated --[[ comment -- truncated");
                    }

                    Emit(skeleton, depth, ' ');
                    i = close;
                    continue;
                }

                // Line comment: to the next line break, either flavour. Lua treats a
                // lone CR as a line break too, so stopping only at '\n' would swallow
                // a real entry on a CR-terminated file.
                while (i < content.Length && content[i] != '\n' && content[i] != '\r')
                {
                    i++;
                }

                Emit(skeleton, depth, ' ');
                continue;
            }

            // --- long strings ---
            if (c == '[')
            {
                var level = LongBracketLevel(content, i);
                if (level >= 0)
                {
                    var close = FindLongBracketClose(content, i, level);
                    if (close < 0)
                    {
                        return new ScanResult(null, null, "unterminated [[ string -- truncated");
                    }

                    Emit(skeleton, depth, 'S');
                    i = close;
                    continue;
                }
            }

            // --- short strings ---
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < content.Length && content[i] != quote)
                {
                    // A raw line break inside a short string is not legal Lua. Letting
                    // it through is how a key could contain a newline, produce a file
                    // WoW refuses to load, and still validate.
                    if (content[i] == '\n' || content[i] == '\r')
                    {
                        return new ScanResult(null, null, "unterminated string -- truncated or malformed");
                    }

                    i += content[i] == '\\' ? 2 : 1;
                }

                if (i >= content.Length)
                {
                    return new ScanResult(null, null, "unterminated string -- truncated or malformed");
                }

                i++;
                Emit(skeleton, depth, 'S');
                continue;
            }

            // --- table structure ---
            if (c == '{')
            {
                depth++;
                if (depth <= 2)
                {
                    skeleton.Append('{');
                }

                if (depth == 2)
                {
                    // The inner tables get their own dedicated parsing; here they
                    // collapse to a placeholder so the shape around them is what
                    // gets compared.
                    skeleton.Append('#');
                }

                openAt[depth] = (pendingName, i + 1);
                pendingName = null;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (depth == 0)
                {
                    return new ScanResult(null, null, "unbalanced braces -- truncated or malformed");
                }

                var (name, start) = openAt.GetValueOrDefault(depth);
                if (depth == 2 && name is not null)
                {
                    // depth 2 == a table directly inside ns.GuildData = { ... }.
                    var table = new LuaTable(content[start..i]);
                    if (name == "meta")
                    {
                        if (meta is not null)
                        {
                            return new ScanResult(null, null, "two meta tables -- the addon would use the second");
                        }

                        meta = table;
                    }
                    else if (name == "ilvls")
                    {
                        if (ilvls is not null)
                        {
                            return new ScanResult(null, null, "two ilvls tables -- the addon would use the second");
                        }

                        ilvls = table;
                    }
                }

                if (depth <= 2)
                {
                    skeleton.Append('}');
                }

                openAt.Remove(depth);
                depth--;
                i++;
                continue;
            }

            // --- a name that might introduce a table ---
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < content.Length && (char.IsLetterOrDigit(content[i]) || content[i] == '_'))
                {
                    i++;
                }

                var word = content[start..i];
                Emit(skeleton, depth, word);

                // Only remember it if what follows is "= {".
                var j = i;
                while (j < content.Length && char.IsWhiteSpace(content[j]))
                {
                    j++;
                }

                if (j < content.Length && content[j] == '=')
                {
                    j++;
                    while (j < content.Length && char.IsWhiteSpace(content[j]))
                    {
                        j++;
                    }

                    if (j < content.Length && content[j] == '{')
                    {
                        pendingName = word;
                    }
                }

                continue;
            }

            Emit(skeleton, depth, c);
            i++;
        }

        if (depth != 0)
        {
            return new ScanResult(null, null, "unbalanced braces -- truncated");
        }

        return new ScanResult(meta, ilvls, null, Collapse(skeleton.ToString()));
    }

    /// <summary>Append to the skeleton, but only for content outside the inner tables.</summary>
    private static void Emit(StringBuilder skeleton, int depth, char c)
    {
        if (depth <= 1)
        {
            skeleton.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }
    }

    private static void Emit(StringBuilder skeleton, int depth, string s)
    {
        if (depth <= 1)
        {
            skeleton.Append(s);
        }
    }

    /// <summary>Runs of whitespace to one space, so formatting is not what is compared.</summary>
    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastWasSpace = false;

        foreach (var c in s)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }
            }
            else
            {
                sb.Append(c);
            }

            lastWasSpace = isSpace;
        }

        return sb.ToString().Trim();
    }

    /// <summary>Level of a Lua long bracket at <paramref name="i"/>, or -1 if there isn't one.</summary>
    private static int LongBracketLevel(string s, int i)
    {
        if (i >= s.Length || s[i] != '[')
        {
            return -1;
        }

        var j = i + 1;
        var level = 0;
        while (j < s.Length && s[j] == '=')
        {
            level++;
            j++;
        }

        return j < s.Length && s[j] == '[' ? level : -1;
    }

    /// <summary>Index just past the matching close bracket, or -1 when it never closes.</summary>
    private static int FindLongBracketClose(string s, int openStart, int level)
    {
        var close = "]" + new string('=', level) + "]";
        var at = s.IndexOf(close, openStart, StringComparison.Ordinal);
        return at < 0 ? -1 : at + close.Length;
    }

    /// <summary>
    /// Reads the meta table's scalar fields, rejecting a duplicate outright.
    /// <para>
    /// Reading with a "first match wins" regex was a real hole: a Lua table
    /// constructor keeps the <i>last</i> assignment, so a file with two
    /// <c>guild</c> or two <c>generated_epoch</c> lines validated against the first
    /// and ran against the second - which is precisely how a wrong guild or an
    /// unbeatable future epoch would slip past the checks written to stop them.
    /// </para>
    /// </summary>
    private static GuildIdentity? ReadIdentity(LuaTable meta, out string? error)
    {
        error = null;

        if (meta.Fields.Count == 0 && !ReadFields(meta, out error))
        {
            return null;
        }

        meta.Fields.TryGetValue("region", out var region);
        meta.Fields.TryGetValue("realm", out var realm);
        meta.Fields.TryGetValue("guild", out var guild);

        if (string.IsNullOrWhiteSpace(region) ||
            string.IsNullOrWhiteSpace(realm) ||
            string.IsNullOrWhiteSpace(guild))
        {
            error = "meta is missing region/realm/guild";
            return null;
        }

        return new GuildIdentity(region, realm, guild);
    }

    /// <summary>
    /// Strict walk of the <c>meta</c> body: <c>name = "string"</c> or
    /// <c>name = number</c>, comma separated, with comments and whitespace allowed
    /// and nothing else. Duplicate names are refused.
    /// <para>
    /// Picking the fields out with a regex, as this used to, ignored everything
    /// between them - so a line break landing inside a comment in this table, or in
    /// the middle of the epoch digits, left a file that balances, parses here, and
    /// is not Lua. The skeleton check cannot see it either: that deliberately
    /// collapses the inner tables. This is the other half of that guarantee.
    /// </para>
    /// <para>
    /// Reading with "first match wins" was a second hole: a Lua table constructor
    /// keeps the <i>last</i> assignment, so a file with two <c>guild</c> or two
    /// <c>generated_epoch</c> lines validated against the first and ran against the
    /// second - exactly how a wrong guild or an unbeatable future epoch would slip
    /// past the checks written to stop them.
    /// </para>
    /// </summary>
    private static bool ReadFields(LuaTable meta, out string? error)
    {
        error = null;

        var body = meta.Body;
        var i = 0;
        var expectSeparator = false;

        while (i < body.Length)
        {
            if (char.IsWhiteSpace(body[i]))
            {
                i++;
                continue;
            }

            if (body[i] == '-' && i + 1 < body.Length && body[i + 1] == '-')
            {
                var level = LongBracketLevel(body, i + 2);
                if (level >= 0)
                {
                    var close = FindLongBracketClose(body, i + 2, level);
                    if (close < 0)
                    {
                        error = "unterminated --[[ comment in the meta table";
                        return false;
                    }

                    i = close;
                    continue;
                }

                while (i < body.Length && body[i] != '\n' && body[i] != '\r')
                {
                    i++;
                }

                continue;
            }

            if (body[i] == ',')
            {
                if (!expectSeparator)
                {
                    error = "stray comma in the meta table";
                    return false;
                }

                expectSeparator = false;
                i++;
                continue;
            }

            if (expectSeparator)
            {
                error = "missing comma in the meta table";
                return false;
            }

            var field = MetaField().Match(body, i);
            if (!field.Success || field.Index != i)
            {
                var preview = body[i..Math.Min(i + 40, body.Length)].Replace('\n', ' ').Trim();
                error = $"unexpected content in the meta table near '{preview}'";
                return false;
            }

            var name = field.Groups[1].Value;
            var value = field.Groups[2].Success ? field.Groups[2].Value : field.Groups[3].Value;

            if (!meta.Fields.TryAdd(name, value))
            {
                error = $"meta assigns {name} twice -- the addon would use the second value";
                return false;
            }

            expectSeparator = true;
            i = field.Index + field.Length;
        }

        return true;
    }

    /// <summary>
    /// Strict parse of the <c>ilvls</c> body: a sequence of <c>["key"] = number</c>
    /// separated by commas, with whitespace and comments allowed and nothing else.
    /// <para>
    /// Counting entries with a loose regex, as this used to, accepted a file that was
    /// brace-balanced but not valid Lua - a missing comma between two entries, say.
    /// WoW then skips the file entirely, <c>ns.GuildData</c> is nil, and the client
    /// shows zero characters and can never adopt a snapshot again, because
    /// <c>Data:IdentityKey()</c> reads the very table that failed to load.
    /// </para>
    /// </summary>
    private static List<(string Key, long Ilvl)>? ParseEntries(string body, out string? error)
    {
        error = null;
        var entries = new List<(string, long)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        var expectSeparator = false;

        while (i < body.Length)
        {
            if (char.IsWhiteSpace(body[i]))
            {
                i++;
                continue;
            }

            if (body[i] == '-' && i + 1 < body.Length && body[i + 1] == '-')
            {
                var level = LongBracketLevel(body, i + 2);
                if (level >= 0)
                {
                    var close = FindLongBracketClose(body, i + 2, level);
                    if (close < 0)
                    {
                        error = "unterminated --[[ comment in the ilvls table";
                        return null;
                    }

                    i = close;
                    continue;
                }

                while (i < body.Length && body[i] != '\n' && body[i] != '\r')
                {
                    i++;
                }

                continue;
            }

            if (body[i] == ',')
            {
                if (!expectSeparator)
                {
                    error = "stray comma in the ilvls table";
                    return null;
                }

                expectSeparator = false;
                i++;
                continue;
            }

            if (expectSeparator)
            {
                // Two entries with nothing between them. Brace counting cannot see
                // this; Lua's parser can, and rejects the whole file.
                error = $"missing comma after entry {entries.Count} in the ilvls table";
                return null;
            }

            var entry = EntryAt().Match(body, i);
            if (!entry.Success || entry.Index != i)
            {
                var preview = body[i..Math.Min(i + 40, body.Length)].Replace('\n', ' ').Trim();
                error = $"unexpected content in the ilvls table near '{preview}'";
                return null;
            }

            var key = entry.Groups[1].Value;
            var raw = entry.Groups[2].Value;

            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ilvl))
            {
                error = $"item level for '{key}' is not a whole number";
                return null;
            }

            if (ilvl < MinIlvl || ilvl > MaxIlvl)
            {
                // Sync.lua drops these per-entry and rejects the whole snapshot past
                // 10% of them. Under that threshold is worse than over: the affected
                // members are silently deleted from every other client's roster while
                // this one still shows them.
                error = $"'{key}' has an out-of-range item level ({ilvl})";
                return null;
            }

            if (Encoding.UTF8.GetByteCount(key) > MaxKeyBytes || !SyncKeyShape().IsMatch(key))
            {
                // Peers drop keys of the wrong shape, and enough of them rejects the
                // whole snapshot guild-wide. '|' is excluded because adopted keys are
                // printed into the chat frame, where "|cffff0000" is markup, not text.
                error = $"'{key}' is not a usable character key";
                return null;
            }

            if (!seen.Add(key))
            {
                error = $"duplicate character key: {key}";
                return null;
            }

            entries.Add((key, ilvl));
            expectSeparator = true;
            i = entry.Index + entry.Length;
        }

        return entries;
    }

    [GeneratedRegex(@"^\s*<")]
    private static partial Regex LeadingAngleBracket();

    [GeneratedRegex(@"ns\.GuildData\s*=")]
    private static partial Regex GuildDataAssignment();

    /// <summary>
    /// <c>[^"\r\n]</c>, not <c>[^"]</c>: a newline inside the key would be accepted
    /// by the looser class and produce a file Lua cannot compile.
    /// </summary>
    [GeneratedRegex(@"\[""([^""\r\n]*)""\]\s*=\s*(-?\d+)")]
    private static partial Regex EntryAt();

    /// <summary>One <c>name = "string"</c> or <c>name = number</c> assignment in meta.</summary>
    [GeneratedRegex(@"([A-Za-z_]\w*)\s*=\s*(?:""([^""\r\n]*)""|(-?\d+))")]
    private static partial Regex MetaField();

    /// <summary>
    /// Sync.lua's <c>validKey</c> pattern, in .NET form.
    /// <para>
    /// <c>\A</c> and <c>\z</c> rather than <c>^</c> and <c>$</c>: .NET's <c>$</c>
    /// matches before a trailing newline, so <c>"Name-realm\n"</c> would pass a
    /// <c>$</c>-anchored check while Lua's own <c>$</c> rejects it.
    /// </para>
    /// <para>
    /// The whitespace set is spelled out rather than written <c>\s</c>, because
    /// .NET's <c>\s</c> is Unicode-aware and Lua's <c>%s</c> is ASCII-only. Using
    /// <c>\s</c> refused keys - a non-breaking space in a character name - that
    /// every peer would have accepted, rejecting the whole roster over one member.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"\A[^\t\n\v\f\r :;=|]+-[A-Za-z0-9-]+\z")]
    private static partial Regex SyncKeyShape();

    /// <summary>
    /// Characters that genuinely break the sync wire header. Sync's own header
    /// captures are <c>[^:;]*</c>, and <c>|</c> is chat markup wherever an adopted
    /// key or guild name is printed. Nothing wider - see the call site.
    /// </summary>
    [GeneratedRegex(@"[:;|]")]
    private static partial Regex HeaderUnsafe();
}
