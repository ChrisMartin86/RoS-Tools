using System.Runtime.Versioning;
using System.Threading;
using RoSTools.Sidecar.Core;

namespace RoSTools.Sidecar;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string MutexName = @"Local\RoSTools.Sidecar.Instance";
    private const string ShowSettingsEventName = @"Local\RoSTools.Sidecar.ShowSettings";

    [STAThread]
    private static int Main()
    {
        // Per-session, not Global\: two users on the same machine each get one.
        using var single = new Mutex(initiallyOwned: true, MutexName, out var isOnlyInstance);

        if (!isOnlyInstance)
        {
            // Hand the running instance the click instead of starting a second tray icon.
            if (EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out var existing))
            {
                using (existing)
                {
                    existing.Set();
                }
            }

            return 0;
        }

        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);

        ApplicationConfiguration.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("unhandled exception", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };

        Application.ThreadException += (_, e) =>
            Log.Error("UI thread exception", e.Exception);

        Log.Info($"starting {GuildDataClient.UserAgent}");

        using var tray = new TrayContext(showSettings);
        Application.Run(tray);

        Log.Info("stopped.");
        return 0;
    }
}
