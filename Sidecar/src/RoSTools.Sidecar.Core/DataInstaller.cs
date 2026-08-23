namespace RoSTools.Sidecar.Core;

/// <summary>
/// Puts a validated file into place. Same shape as the PowerShell updater: keep
/// one rollback copy, then replace in a single move so a crash mid-write cannot
/// leave half a file where the addon expects Lua.
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

        if (File.Exists(destinationPath))
        {
            try
            {
                File.Copy(destinationPath, destinationPath + ".bak", overwrite: true);
            }
            catch (Exception ex)
            {
                // A missing rollback copy is worth a log line, not a failed update.
                Log.Warn($"could not write the .bak rollback copy: {ex.Message}");
            }
        }

        // File.Move handles a cross-volume move (temp on C:, WoW on D:) by
        // falling back to copy-then-delete.
        File.Move(stagingPath, destinationPath, overwrite: true);
    }
}
