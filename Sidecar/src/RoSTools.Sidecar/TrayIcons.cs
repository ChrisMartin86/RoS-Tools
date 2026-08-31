using System.Reflection;
using System.Runtime.Versioning;
using RoSTools.Sidecar.Core;

namespace RoSTools.Sidecar;

public enum TrayState
{
    Idle,
    Checking,

    /// <summary>
    /// Working, but something needs attention - checks have stopped happening, or
    /// the installed roster is aging. Shares the error artwork deliberately: with
    /// notifications off, "looks normal" is indistinguishable from "is fine", and
    /// the whole point of this state is to break that tie.
    /// </summary>
    Warning,

    Error,
}

/// <summary>
/// The three tray icons, loaded once from embedded resources. With notifications
/// switched off by design, the icon and its tooltip are the only way a failure
/// reaches the user, so a missing resource falls back to something visible rather
/// than to no icon at all.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayIcons
{
    private static readonly Lazy<Icon> IdleIcon = new(() => Load("ros.ico"));
    private static readonly Lazy<Icon> CheckingIcon = new(() => Load("ros-checking.ico"));
    private static readonly Lazy<Icon> ErrorIcon = new(() => Load("ros-error.ico"));

    public static Icon For(TrayState state) => state switch
    {
        TrayState.Checking => CheckingIcon.Value,
        TrayState.Warning or TrayState.Error => ErrorIcon.Value,
        _ => IdleIcon.Value,
    };

    private static Icon Load(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (name is not null)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is not null)
                {
                    return new Icon(stream);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"could not load the {fileName} resource: {ex.Message}");
            }
        }

        return SystemIcons.Application;
    }
}
