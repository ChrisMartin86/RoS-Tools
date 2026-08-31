namespace RoSTools.Sidecar.Core;

/// <summary>
/// Where the sidecar keeps its own state: <c>%LOCALAPPDATA%\RoS-Tools</c>. The
/// retired PowerShell updater used the same folder with <c>updater-state.json</c>;
/// the file name here stays distinct so a leftover from that era is never read as
/// sidecar state.
/// </summary>
public static class Paths
{
    public static string StateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RoS-Tools");

    public static string LogDirectory => Path.Combine(StateDirectory, "logs");

    public static string SettingsFile => Path.Combine(StateDirectory, "sidecar.json");

    public static void EnsureStateDirectory() => Directory.CreateDirectory(StateDirectory);
}
