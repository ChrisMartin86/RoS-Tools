using System.Runtime.Versioning;
using System.Threading;
using RoSTools.Sidecar.Core;

namespace RoSTools.Sidecar;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string MutexName = @"Local\RoSTools.Sidecar.Instance";
    private const string ShowSettingsEventName = @"Local\RoSTools.Sidecar.ShowSettings";
    private const string QuitEventName = @"Local\RoSTools.Sidecar.Quit";

    [STAThread]
    private static int Main()
    {
        // Both of these come first, before anything that can throw. An elevation
        // mismatch on the named handles below used to throw out of Main with no
        // handler installed and no visual styles set, so the only thing the user saw
        // was the CLR's unhandled-exception dialog.
        ApplicationConfiguration.Initialize();
        InstallGlobalHandlers();

        EventWaitHandle? showSettings = null;
        EventWaitHandle? quit = null;
        Mutex? single = null;
        bool isOnlyInstance;

        try
        {
            // The events are created BEFORE the mutex is claimed, and deliberately so.
            // Claiming the mutex first opens a window - visual styles, handler wiring,
            // whatever else grows here later - in which a second launch loses the race
            // for the mutex and then finds no event to signal, so it exits having done
            // nothing at all: no window, no tray icon, no message. Two quick
            // double-clicks, or the installer's Start-Process racing a shortcut the
            // user just clicked, both land in it. Creating the events first means that
            // by the time anyone can lose the mutex race, there is something to signal.
            showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName, out _);
            quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName, out _);

            // Per-session, not Global\: two users on the same machine each get one.
            single = new Mutex(initiallyOwned: true, MutexName, out isOnlyInstance);
        }
        catch (Exception ex)
        {
            showSettings?.Dispose();
            quit?.Dispose();
            single?.Dispose();

            Log.Error("could not claim the single-instance handles", ex);
            ReportStartupFailure(ex);
            return 1;
        }

        try
        {
            if (!isOnlyInstance)
            {
                quit.Dispose();
                HandOffToRunningInstance(showSettings);
                return 0;
            }

            Log.Info($"starting {GuildDataClient.UserAgent}");

            using var tray = new TrayContext(showSettings, quit);
            Application.Run(tray);

            Log.Info("stopped.");
            return 0;
        }
        finally
        {
            single.Dispose();
        }
    }

    private static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("unhandled exception", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };

        Application.ThreadException += (_, e) =>
            Log.Error("UI thread exception", e.Exception);
    }

    /// <summary>
    /// Hands the running instance the click instead of starting a second tray icon.
    /// <para>
    /// The handle is the one created above, so there is nothing to open and nothing
    /// to race: signalling it is the whole job. The event is auto-reset and its
    /// initial state is ignored when the object already exists, so a signal raised
    /// before the first instance has registered its wait is still there when it does.
    /// </para>
    /// <para>
    /// The event is deliberately not waited on afterwards to see whether anyone
    /// consumed it: a third launch arriving inside that window would have its own
    /// signal eaten by this process's wait, which trades a silent failure for a
    /// stolen one.
    /// </para>
    /// </summary>
    private static void HandOffToRunningInstance(EventWaitHandle showSettings)
    {
        using (showSettings)
        {
            try
            {
                showSettings.Set();
                Log.Info("another instance is already running; asked it to show its settings window.");
                return;
            }
            catch (Exception ex)
            {
                Log.Error("could not signal the running instance", ex);
            }
        }

        // Exiting silently here is what made this invisible before: no window, no
        // tray icon, nothing in the UI to explain why a double-click did nothing.
        MessageBox.Show(
            "RoS-Tools Sidecar is already running, but this copy could not reach it.\n\n" +
            "Look for its icon in the notification area. If it is not there, end the " +
            "RoSToolsSidecar process in Task Manager and start the sidecar again.",
            "RoS-Tools Sidecar",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void ReportStartupFailure(Exception ex)
    {
        // UnauthorizedAccessException here means the named objects exist but were
        // created at an integrity level this process cannot open - almost always a
        // copy started elevated (Install-Sidecar.ps1 advises re-running elevated,
        // and then launches the exe itself) with a normal-IL launch arriving after.
        var body = ex is UnauthorizedAccessException
            ? "RoS-Tools Sidecar is already running with different Windows privileges " +
              "(most likely as an administrator), so this copy cannot reach it.\n\n" +
              "Quit the running copy from its tray icon, then start the sidecar again " +
              "without elevation."
            : $"RoS-Tools Sidecar could not start:\n\n{ex.Message}";

        MessageBox.Show(body, "RoS-Tools Sidecar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
