using System.Globalization;

namespace RoSTools.Sidecar.Core;

/// <summary>
/// A deliberately small rolling text log. The sidecar shows no notifications,
/// so when something goes wrong this file and the tray tooltip are the only
/// places a user can find out why.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;

    private static readonly Lock Gate = new();

    /// <summary>Overridable so tests do not write into the real profile.</summary>
    public static string? DirectoryOverride { get; set; }

    public static string Directory => DirectoryOverride ?? Paths.LogDirectory;

    private static string Current => Path.Combine(Directory, "sidecar.log");

    private static string Previous => Path.Combine(Directory, "sidecar.1.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss} {1,-5} {2}{3}",
            DateTime.Now,
            level,
            message,
            Environment.NewLine);

        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                var info = new FileInfo(Current);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(Current, Previous, overwrite: true);
                }

                File.AppendAllText(Current, line);
            }
            catch
            {
                // Logging must never be the thing that takes the app down.
            }
        }
    }
}
