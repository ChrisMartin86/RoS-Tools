using System.Runtime.Versioning;
using Microsoft.Win32;
using RoSTools.Sidecar.Core;

namespace RoSTools.Sidecar;

/// <summary>
/// Start-with-Windows via the per-user Run key. HKCU deliberately, not HKLM or a
/// scheduled task: no elevation, nothing left behind for other accounts.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RoS-Tools Sidecar";

    /// <summary>
    /// True only when the Run key points at <i>this</i> executable.
    /// <para>
    /// "Some non-empty string is there" is not the same question. The exe can be
    /// moved by anything that is not <c>Install-Sidecar.ps1</c> - a manual copy to a
    /// new folder, a profile move, a restore from backup - and the stale entry then
    /// launches nothing at sign-in while this window keeps the box ticked. Comparing
    /// against <see cref="Environment.ProcessPath"/> is what lets
    /// <see cref="Reassert"/> notice and repair it.
    /// </para>
    /// </summary>
    public static bool IsEnabled()
    {
        var stored = AutoStartPolicy.ExecutablePathOf(ReadValue());
        if (stored is null)
        {
            return false;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            // Nothing to compare against. An entry exists and we cannot prove it
            // wrong, so report it as enabled rather than silently unticking a box
            // the user did tick.
            Log.Warn("could not determine the executable path; the Run key was not verified.");
            return true;
        }

        return SamePath(stored, exe);
    }

    /// <summary>
    /// Repairs the Run value at startup when the user wants to start with Windows and
    /// the stored command would launch nothing. Without this nothing ever repairs a
    /// moved exe except the installer, and the failure is silent: Windows launches
    /// nothing, the tray never appears, and the roster this machine seeds for the
    /// guild quietly stops advancing.
    /// <para>
    /// Repair, not reassert. See <see cref="AutoStartPolicy.ShouldRepair"/> for why an
    /// entry pointing at a different copy that still exists is left exactly where it
    /// is: rewriting on mismatch turned "run the dev build once" into "sign-in launch
    /// breaks at the next clean".
    /// </para>
    /// </summary>
    public static void Reassert(bool startWithWindows)
    {
        if (!startWithWindows)
        {
            return;
        }

        var stored = ReadValue();
        if (!AutoStartPolicy.ShouldRepair(stored))
        {
            return;
        }

        Log.Info(stored is null
            ? "there is no Run entry; pointing it at this executable."
            : "the Run entry points at a program that is no longer there; repointing it at " +
              "this executable.");

        Set(true);
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    Log.Warn("could not determine the executable path; autostart not set.");
                    return;
                }

                key.SetValue(ValueName, $"\"{exe}\"");
                Log.Info("autostart enabled.");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("autostart disabled.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("could not update the Run key", ex);
        }
    }

    private static string? ReadValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            // A denied or corrupt key is not fatal: report "not enabled", which makes
            // the caller re-write rather than trust an entry it could not read.
            Log.Warn($"could not read the Run key: {ex.Message}");
            return null;
        }
    }

    private static bool SamePath(string stored, string exe)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(stored),
                Path.GetFullPath(exe),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A hand-edited value that is not a usable path cannot be pointing here.
            Log.Warn($"the Run entry is not a usable path: {ex.Message}");
            return false;
        }
    }
}
