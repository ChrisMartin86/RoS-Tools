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

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the Run key: {ex.Message}");
            return false;
        }
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
}
