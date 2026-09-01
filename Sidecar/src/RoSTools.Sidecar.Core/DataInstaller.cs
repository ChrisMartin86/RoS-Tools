namespace RoSTools.Sidecar.Core;

/// <summary>
/// Puts a validated file into place. Same shape as the PowerShell updater: keep
/// one rollback copy, then replace in a single same-volume move so a crash
/// mid-write cannot leave half a file where the addon expects Lua.
/// </summary>
public static class DataInstaller
{
    /// <param name="stagingPath">A file that has already passed validation.</param>
    /// <param name="destinationPath">The addon's <c>Data\GuildData.lua</c>.</param>
    public static void Install(string stagingPath, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var backup = destinationPath + ".bak";

        // Whether the .bak is a copy of the file this call is about to replace, as
        // opposed to whatever happened to be lying there from an earlier run. Only
        // the first can honestly be called "the previous roster"; the second can be
        // arbitrarily old, and saying otherwise in the log sends whoever is reading
        // it looking for a corruption that never happened.
        var backupIsCurrent = false;
        bool hadBackup;

        // Only a destination worth rolling back to is worth copying over the .bak.
        //
        // Validation ran on the staging file; nothing ever looked at the destination.
        // So a CurseForge update or an interrupted third-party write that truncated
        // GuildData.lua got copied straight over the last known-good .bak on the very
        // next install, and the one file that could have put the roster back was
        // gone. Refusing the copy leaves the older - but loadable - .bak in place,
        // which is the whole point of having one.
        if (File.Exists(destinationPath))
        {
            if (WorthKeeping(destinationPath))
            {
                try
                {
                    File.Copy(destinationPath, backup, overwrite: true);
                    backupIsCurrent = true;
                    hadBackup = true;
                }
                catch (Exception ex)
                {
                    // A missing rollback copy is worth a log line, not a failed update.
                    Log.Warn($"could not write the .bak rollback copy: {ex.Message}");
                    hadBackup = File.Exists(backup);
                }
            }
            else
            {
                hadBackup = File.Exists(backup);
            }
        }
        else
        {
            hadBackup = File.Exists(backup);
        }

        // Land the bytes on the destination's own volume first.
        //
        // Staging lives in %TEMP%, which is on the system drive, so for a WoW
        // install on any other drive File.Move degrades to a copy over the
        // destination - not atomic. A drive that drops, a disk that fills or an
        // antivirus lock partway through that copy leaves truncated Lua where the
        // roster was, and the addon then fails to load its data entirely.
        var sameVolume = destinationPath + ".new";

        try
        {
            TryDelete(sameVolume);
            File.Copy(stagingPath, sameVolume, overwrite: true);

            // Within one volume this is a rename: atomic, all or nothing.
            File.Move(sameVolume, destinationPath, overwrite: true);
        }
        catch
        {
            TryDelete(sameVolume);

            // The destination is only ever replaced by the rename above, so it is
            // still intact here. Restore anyway if it somehow is not - the cost is
            // one file copy and the alternative is an addon that cannot load.
            if (hadBackup && !LooksInstalled(destinationPath))
            {
                try
                {
                    File.Copy(backup, destinationPath, overwrite: true);

                    Log.Warn(backupIsCurrent
                        ? "install failed; restored the previous roster from .bak."
                        : "install failed; restored from a .bak this run did not write -- it " +
                          "may predate the file it just replaced.");
                }
                catch (Exception restoreEx)
                {
                    Log.Error("install failed and the .bak could not be restored", restoreEx);
                }
            }

            throw;
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    /// <summary>
    /// Whether the file at the destination is worth copying to <c>.bak</c> before it
    /// is replaced.
    /// <para>
    /// Not the same question as <see cref="GuildDataValidator.Validate"/>, and that
    /// is the point. Validation refuses a roster whose <c>generated_epoch</c> is more
    /// than five minutes ahead of this machine's clock - which is a statement about
    /// the two clocks, not about the file. A DST misconfiguration, a resumed VM or an
    /// NTP correction is enough, and treating that as damage meant a perfectly good
    /// roster was replaced with no rollback copy taken at all.
    /// </para>
    /// <para>
    /// So: structurally intact with entries in it is enough to be worth keeping.
    /// Truncated, HTML, or anything else the parser cannot get characters out of is
    /// not, and the older .bak stays where it is.
    /// </para>
    /// </summary>
    private static bool WorthKeeping(string path)
    {
        var installed = GuildDataValidator.Validate(path);
        if (installed.Ok)
        {
            return true;
        }

        var entries = GuildDataValidator.EntriesOf(path);
        if (entries is { Count: > 0 })
        {
            Log.Warn(
                $"the installed roster does not validate ({installed.Reason}), but it parses " +
                $"with {entries.Count} characters in it; keeping it as the rollback copy.");
            return true;
        }

        Log.Warn(
            $"the installed roster is not usable ({installed.Reason}); keeping the " +
            "existing .bak rather than overwriting it with damaged data.");

        return false;
    }

    /// <summary>
    /// Whether the destination is a roster the addon could actually load. "Non-empty"
    /// was not that check: a truncated GuildData.lua is non-empty, so the restore
    /// after a failed move looked at the damage and decided nothing was wrong.
    /// </summary>
    private static bool LooksInstalled(string path)
    {
        try
        {
            return File.Exists(path) && GuildDataValidator.Validate(path).Ok;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                // Nothing creates a directory here, but if one ever appears every
                // future install fails identically forever, because File.Copy cannot
                // write over it and a file-only delete silently does nothing.
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not remove {path}: {ex.Message}");
        }
    }
}
