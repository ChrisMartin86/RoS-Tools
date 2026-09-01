using RoSTools.Sidecar.Core;
using Xunit;

namespace RoSTools.Sidecar.Tests;

public class DataInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-installer-" + Guid.NewGuid().ToString("N"));

    public DataInstallerTests()
    {
        Directory.CreateDirectory(_root);
        Log.DirectoryOverride = Path.Combine(_root, "logs");
    }

    /// <summary>
    /// A real generated roster. The rollback copy is only taken from a destination
    /// that still validates, so these tests have to install files that do.
    /// </summary>
    private static string Roster(long epoch = 1787509007L, string ilvl = "302") =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "valid.lua"))
            .Replace("generated_epoch = 1787509007", $"generated_epoch = {epoch}", StringComparison.Ordinal)
            .Replace("[\"Icebyte-moon-guard\"] = 302", $"[\"Icebyte-moon-guard\"] = {ilvl}", StringComparison.Ordinal);

    [Fact]
    public void Creates_the_Data_folder_when_it_is_missing()
    {
        var roster = Roster();
        var staging = Stage(roster);
        var destination = Path.Combine(_root, "RoS-Tools", "Data", "GuildData.lua");

        DataInstaller.Install(staging, destination);

        Assert.Equal(roster, File.ReadAllText(destination));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public void Keeps_one_rollback_copy_of_the_previous_roster()
    {
        var destination = Path.Combine(_root, "GuildData.lua");
        var old = Roster(ilvl: "301");
        File.WriteAllText(destination, old);

        var fresh = Roster(ilvl: "302");
        DataInstaller.Install(Stage(fresh), destination);

        Assert.Equal(fresh, File.ReadAllText(destination));
        Assert.Equal(old, File.ReadAllText(destination + ".bak"));
    }

    [Fact]
    public void Replacing_twice_leaves_the_bak_one_generation_behind()
    {
        var destination = Path.Combine(_root, "GuildData.lua");
        var first = Roster(ilvl: "301");
        var second = Roster(ilvl: "302");

        DataInstaller.Install(Stage(first), destination);
        DataInstaller.Install(Stage(second), destination);

        Assert.Equal(second, File.ReadAllText(destination));
        Assert.Equal(first, File.ReadAllText(destination + ".bak"));
    }

    [Fact]
    public void A_truncated_destination_does_not_overwrite_a_good_rollback_copy()
    {
        // The bug: validation only ever ran on the staging file, so whatever happened
        // to be sitting at the destination was copied over the .bak unexamined. A
        // CurseForge update or an interrupted third-party write truncates
        // GuildData.lua, the next install copies that truncation over the last good
        // rollback, and the one file that could have restored the roster is gone.
        var destination = Path.Combine(_root, "GuildData.lua");
        var good = Roster(ilvl: "301");

        DataInstaller.Install(Stage(good), destination);
        Assert.False(File.Exists(destination + ".bak"));

        // Something else truncates what we installed. Non-empty, so the old
        // "length > 0" notion of a usable file saw nothing wrong with it.
        var truncated = good[..300];
        File.WriteAllText(destination, truncated);
        Assert.NotEmpty(truncated);

        DataInstaller.Install(Stage(Roster(ilvl: "302")), destination);

        Assert.False(
            File.Exists(destination + ".bak"),
            "a truncated destination must not become the rollback copy");
    }

    [Fact]
    public void A_truncated_destination_leaves_an_older_good_rollback_intact()
    {
        var destination = Path.Combine(_root, "GuildData.lua");
        var generationOne = Roster(ilvl: "301");
        var generationTwo = Roster(ilvl: "302");

        DataInstaller.Install(Stage(generationOne), destination);
        DataInstaller.Install(Stage(generationTwo), destination);
        Assert.Equal(generationOne, File.ReadAllText(destination + ".bak"));

        File.WriteAllText(destination, generationTwo[..300]);
        DataInstaller.Install(Stage(Roster(ilvl: "303")), destination);

        Assert.Equal(generationOne, File.ReadAllText(destination + ".bak"));
    }

    [Fact]
    public void A_destination_with_an_implausible_epoch_is_still_backed_up()
    {
        // Validate refuses a roster whose generated_epoch is more than five minutes
        // ahead of this machine's clock, which is a statement about the two clocks -
        // a DST misconfiguration, a resumed VM, an NTP correction - and not about the
        // file. Treating it as damage meant no .bak was taken at all, so a perfectly
        // good roster was replaced with nothing to roll back to.
        var destination = Path.Combine(_root, "GuildData.lua");
        var skewed = Roster(epoch: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600, ilvl: "301");
        File.WriteAllText(destination, skewed);

        Assert.False(
            GuildDataValidator.Validate(destination).Ok,
            "the fixture must be one validation refuses, or this asserts nothing");

        var fresh = Roster(ilvl: "302");
        DataInstaller.Install(Stage(fresh), destination);

        Assert.Equal(fresh, File.ReadAllText(destination));
        Assert.Equal(skewed, File.ReadAllText(destination + ".bak"));
    }

    [Fact]
    public async Task A_restore_from_a_bak_this_run_did_not_write_says_so()
    {
        // hadBackup came from File.Exists(backup) in every branch, so the rollback
        // path could write an arbitrarily old .bak over the destination while logging
        // "restored the previous roster" - sending whoever reads that log looking for
        // a corruption that never happened, and hiding how much roster went missing.
        var destination = Path.Combine(_root, "GuildData.lua");
        var generationOne = Roster(ilvl: "301");

        DataInstaller.Install(Stage(generationOne), destination);
        DataInstaller.Install(Stage(Roster(ilvl: "302")), destination);
        Assert.Equal(generationOne, File.ReadAllText(destination + ".bak"));

        // Something truncates what is installed, so this run takes no copy of it and
        // the .bak that survives is a generation older than the file being replaced.
        File.WriteAllText(destination, Roster(ilvl: "302")[..300]);

        // An install that fails: the staging file is not there to copy from.
        var missing = Path.Combine(_root, "never-written.staging");
        Assert.ThrowsAny<IOException>(() => DataInstaller.Install(missing, destination));

        Assert.Equal(generationOne, File.ReadAllText(destination));

        var log = await File.ReadAllTextAsync(Path.Combine(Log.Directory, "sidecar.log"));
        Assert.Contains("may predate", log, StringComparison.Ordinal);
    }

    private string Stage(string content)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".staging");
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Log.DirectoryOverride = null;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
