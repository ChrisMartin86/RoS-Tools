using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RoSTools.Sidecar.Core;

/// <summary>
/// Finds the installed addon folder: the uninstall registry first, because that
/// survives non-default install locations, then the usual suspects on every fixed
/// drive. (Originally a port of <c>Find-WowRoot</c> / <c>Resolve-AddOnPath</c>
/// from the PowerShell updater that was retired on 2026-08-29.)
/// <para>
/// This type reads the registry and enumerates directories. It must never open,
/// inspect or enumerate the WoW process itself - that is the line between a file
/// updater and something Blizzard would action.
/// </para>
/// </summary>
public static class AddOnLocator
{
    public const string AddOnFolderName = "RoS-Tools";
    public const string TocFileName = "RoS-Tools.toc";

    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft",
    ];

    private static readonly string[] RelativeInstallCandidates =
    [
        @"Program Files (x86)\World of Warcraft\_retail_",
        @"Program Files\World of Warcraft\_retail_",
        @"World of Warcraft\_retail_",
        @"Games\World of Warcraft\_retail_",
        @"Battle.net\World of Warcraft\_retail_",
    ];

    /// <summary>The <c>_retail_</c> folder, or null if it could not be found.</summary>
    public static string? FindWowRetailRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var fromRegistry = FindViaRegistry();
            if (fromRegistry is not null)
            {
                return fromRegistry;
            }
        }

        foreach (var root in FixedDriveRoots())
        {
            foreach (var relative in RelativeInstallCandidates)
            {
                var candidate = Path.Combine(root, relative);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>The installed <c>RoS-Tools</c> addon folder, or null.</summary>
    public static string? FindAddOnFolder()
    {
        var wowRoot = FindWowRetailRoot();
        if (wowRoot is null)
        {
            return null;
        }

        var addOn = Path.Combine(wowRoot, "Interface", "AddOns", AddOnFolderName);
        return LooksLikeAddOnFolder(addOn) ? addOn : null;
    }

    /// <summary>
    /// A folder only counts if the manifest sits directly inside it. This is what
    /// stops a user browsing to <c>Interface\AddOns</c> and quietly getting a
    /// <c>GuildData.lua</c> written somewhere the addon never reads.
    /// </summary>
    public static bool LooksLikeAddOnFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, TocFileName));

    /// <summary>The data file the sidecar writes. The only path it ever touches.</summary>
    public static string DataFileFor(string addOnFolder) =>
        Path.Combine(addOnFolder, "Data", "GuildData.lua");

    [SupportedOSPlatform("windows")]
    private static string? FindViaRegistry()
    {
        foreach (var key in UninstallKeys)
        {
            try
            {
                using var handle = Registry.LocalMachine.OpenSubKey(key);
                if (handle?.GetValue("InstallLocation") is not string location ||
                    string.IsNullOrWhiteSpace(location))
                {
                    continue;
                }

                var candidate = Path.Combine(location, "_retail_");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"registry probe of {key} failed: {ex.Message}");
            }
        }

        return null;
    }

    private static IEnumerable<string> FixedDriveRoots()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            Log.Warn($"could not enumerate drives: {ex.Message}");
            yield break;
        }

        foreach (var drive in drives)
        {
            var ready = false;
            try
            {
                ready = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch
            {
                // A drive that throws on inspection is not one we can search.
            }

            if (ready)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }
}
