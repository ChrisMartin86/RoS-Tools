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
        var hadBackup = false;

        if (File.Exists(destinationPath))
        {
            try
            {
                File.Copy(destinationPath, backup, overwrite: true);
                hadBackup = true;
            }
            catch (Exception ex)
            {
                // A missing rollback copy is worth a log line, not a failed update.
                Log.Warn($"could not write the .bak rollback copy: {ex.Message}");
            }
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
                    Log.Warn("install failed; restored the previous roster from .bak.");
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

    private static bool LooksInstalled(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
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
