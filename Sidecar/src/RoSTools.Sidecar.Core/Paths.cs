namespace RoSTools.Sidecar.Core;

/// <summary>
/// Where the sidecar keeps its own state. Deliberately the same
/// <c>%LOCALAPPDATA%\RoS-Tools</c> folder that <c>Tools\Update-RoSTools.ps1</c>
/// uses, but a different file name - the script owns <c>updater-state.json</c>
/// and the two cache ETags for what may be different destinations.
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
