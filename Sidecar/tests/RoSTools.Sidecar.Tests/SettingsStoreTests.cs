using RoSTools.Sidecar.Core;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// The property under test: a settings file the store could not read is never the
/// last copy of itself. <c>UpdateService.Record</c> saves on every single check, so
/// "fall back to defaults" is one check away from "the file is gone" - taking
/// <c>BlizzardClientSecretProtected</c> with it, which nothing else on the machine
/// holds. The trigger does not have to be corruption: an antivirus or backup agent
/// holding the file <c>FileShare.None</c> at logon reads exactly the same way here
/// and is over a second later.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private const string Secret = "AQAAANCMnd8BFdERjHoAwE_Cl-sBAAAA-this-is-the-dpapi-blob";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-settings-" + Guid.NewGuid().ToString("N"));

    private readonly string _path;

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "sidecar.json");
        Log.DirectoryOverride = Path.Combine(_root, "logs");
    }

    private string Bad => _path + ".bad";

    /// <summary>
    /// Settings that carry the secret and that the store cannot turn into a
    /// <see cref="SidecarSettings"/> - a trailing comma and a stray brace, which is
    /// what a half-written or half-restored file looks like. Everything the user
    /// would have to re-enter is still legible in it, which is the point.
    /// </summary>
    private static string UnreadableSettings =>
        "{\n" +
        "  \"addOnPath\": \"C:\\\\Games\\\\WoW\\\\_retail_\\\\Interface\\\\AddOns\\\\RoS-Tools\",\n" +
        "  \"blizzardClientSecretProtected\": \"" + Secret + "\",\n" +
        "  \"pollIntervalHours\": 6,\n" +
        "}}\n";

    [Fact]
    public void A_first_run_with_no_file_is_not_an_error()
    {
        var store = new SettingsStore(_path);

        var settings = store.Load();

        Assert.Null(settings.LastError);
        Assert.False(File.Exists(Bad), "a missing file is a first run, not a failure");
        Assert.Equal(SidecarSettings.DefaultDataUrl, settings.DataUrl);
    }

    [Fact]
    public void An_unreadable_file_is_moved_aside_before_defaults_take_over()
    {
        File.WriteAllText(_path, UnreadableSettings);

        var settings = new SettingsStore(_path).Load();

        Assert.Contains(Secret, File.ReadAllText(Bad), StringComparison.Ordinal);
        Assert.False(File.Exists(_path), "the original must be out of the way of the next save");
        Assert.Null(settings.BlizzardClientSecretProtected);
    }

    [Fact]
    public void An_unreadable_file_is_surfaced_on_LoadFailure()
    {
        File.WriteAllText(_path, UnreadableSettings);

        var store = new SettingsStore(_path);
        store.Load();

        Assert.NotNull(store.LoadFailure);
        Assert.Contains(".bad", store.LoadFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_load_failure_is_not_erased_by_the_first_successful_check()
    {
        // Where this warning used to live was SidecarSettings.LastError, which
        // UpdateService.Record assigns on every single check - null included. So the
        // one notice that the user's client secret is now in a .bad file survived the
        // thirty-second startup delay and was then wiped by the first poll that
        // worked, silently. It has to outlive that, so the tray can go on showing it.
        File.WriteAllText(_path, UnreadableSettings);

        var store = new SettingsStore(_path);
        store.Load();
        Assert.NotNull(store.LoadFailure);

        // Exactly what Record does when a check succeeds.
        store.Update(s =>
        {
            s.LastCheckUtc = DateTimeOffset.UtcNow;
            s.LastError = null;
        });

        Assert.NotNull(store.LoadFailure);
        Assert.Contains(Secret, File.ReadAllText(Bad), StringComparison.Ordinal);
    }

    [Fact]
    public void A_save_after_a_failed_load_cannot_destroy_the_secret()
    {
        // The actual failure. Every check calls Update -> Save, so within seconds of
        // the failed load the defaults were written straight over the only copy of
        // the DPAPI blob and it was unrecoverable.
        File.WriteAllText(_path, UnreadableSettings);

        var store = new SettingsStore(_path);
        store.Load();

        store.Update(s => s.LastCheckUtc = DateTimeOffset.UtcNow);
        store.Save();

        Assert.DoesNotContain(Secret, File.ReadAllText(_path), StringComparison.Ordinal);
        Assert.Contains(Secret, File.ReadAllText(Bad), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_deserializes_to_nothing_is_still_moved_aside()
    {
        // JSON "null" parses fine and yields no settings. The old code treated that
        // as indistinguishable from a first run.
        File.WriteAllText(_path, "null");

        var store = new SettingsStore(_path);
        store.Load();

        Assert.True(File.Exists(Bad));
        Assert.NotNull(store.LoadFailure);
    }

    [Fact]
    public void A_second_failure_does_not_overwrite_the_first_quarantine_copy()
    {
        // This is the sequence, and the previous test asserted the wrong end of it:
        // the real settings go to .bad, the next check writes a fresh defaults-only
        // sidecar.json, and a second read failure then quarantined *that* worthless
        // file over the only copy of the user's secret. Keeping one .bad is not a
        // feature when the newer one is empty.
        File.WriteAllText(_path, UnreadableSettings);
        new SettingsStore(_path).Load();
        Assert.Contains(Secret, File.ReadAllText(Bad), StringComparison.Ordinal);

        File.WriteAllText(_path, "{ a later, worthless file }");
        new SettingsStore(_path).Load();

        Assert.Contains(Secret, File.ReadAllText(Bad), StringComparison.Ordinal);

        var copies = Directory.GetFiles(_root, "sidecar.json*" + SettingsStore.QuarantineSuffix);
        Assert.Equal(2, copies.Length);
        Assert.Contains(
            copies,
            path => File.ReadAllText(path) == "{ a later, worthless file }");
    }

    [Fact]
    public void A_file_that_can_be_neither_read_nor_moved_survives_the_checks_that_follow()
    {
        // The case the whole class exists for, and the one the quarantine cannot
        // cover: an antivirus or backup agent holding sidecar.json FileShare.None at
        // logon fails the read *and* the rename, because a file another process holds
        // without share-delete cannot be renamed either. The quarantine returns
        // false, Current becomes defaults - and then UpdateService.Record calls
        // Update on every check, so ~30 seconds later the poll loop wrote defaults
        // over the still-present good file. Unprompted, while the tray was telling
        // the user not to change anything for exactly that reason.
        File.WriteAllText(_path, UnreadableSettings);

        var store = new SettingsStore(_path)
        {
            MoveAside = static (_, _) =>
                throw new IOException("the process cannot access the file: it is in use"),
        };

        store.Load();

        Assert.True(store.SavesSuspended);
        Assert.False(File.Exists(Bad), "nothing was quarantined; the file is still where it was");

        // Two checks' worth of Record, plus a direct save for good measure.
        store.Update(s => s.LastCheckUtc = DateTimeOffset.UtcNow);
        store.Update(s => s.LastError = null);
        store.Save();

        Assert.Equal(UnreadableSettings, File.ReadAllText(_path));
        Assert.Contains(Secret, File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void Suspended_saving_resumes_once_the_file_it_was_protecting_is_gone()
    {
        // The other half: a guard that latches forever is its own bug. Once there is
        // nothing left to overwrite there is nothing left to protect, and settings
        // have to start persisting again without a restart.
        File.WriteAllText(_path, UnreadableSettings);

        var store = new SettingsStore(_path)
        {
            MoveAside = static (_, _) => throw new IOException("in use"),
        };

        store.Load();
        Assert.True(store.SavesSuspended);

        // Whatever was holding it let go, and the file went with it.
        File.Delete(_path);

        store.Update(s => s.PollIntervalHours = 12);

        Assert.False(store.SavesSuspended);
        Assert.Equal(12, new SettingsStore(_path).Load().PollIntervalHours);
    }

    [Fact]
    public void A_load_that_succeeds_lifts_a_suspension_from_an_earlier_one()
    {
        File.WriteAllText(_path, UnreadableSettings);

        var store = new SettingsStore(_path)
        {
            MoveAside = static (_, _) => throw new IOException("in use"),
        };

        store.Load();
        Assert.True(store.SavesSuspended);
        Assert.NotNull(store.LoadFailure);

        // The lock is gone and the file reads fine now.
        File.WriteAllText(_path, "{ \"pollIntervalHours\": 8 }");

        Assert.Equal(8, store.Load().PollIntervalHours);
        Assert.False(store.SavesSuspended);
        Assert.Null(store.LoadFailure);

        store.Update(s => s.PollIntervalHours = 9);
        Assert.Equal(9, new SettingsStore(_path).Load().PollIntervalHours);
    }

    [Fact]
    public void A_readable_file_is_left_exactly_where_it_is()
    {
        var store = new SettingsStore(_path);
        store.Load();
        store.Update(s => s.BlizzardClientSecretProtected = Secret);

        var reloaded = new SettingsStore(_path);
        var settings = reloaded.Load();

        Assert.Equal(Secret, settings.BlizzardClientSecretProtected);
        Assert.Null(settings.LastError);
        Assert.False(File.Exists(Bad));
    }

    [Fact]
    public void The_quarantine_path_is_the_settings_file_plus_bad()
    {
        Assert.Equal(_path + ".bad", new SettingsStore(_path).QuarantinePath);
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
