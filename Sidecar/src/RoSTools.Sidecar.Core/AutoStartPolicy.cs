namespace RoSTools.Sidecar.Core;

/// <summary>
/// When startup is allowed to rewrite the per-user Run entry, and when it has to
/// leave it alone.
/// <para>
/// This lives in Core, apart from <c>AutoStart</c> itself, because <c>AutoStart</c>
/// is in the WinForms executable and the test assembly cannot reference a
/// <c>net10.0-windows</c> project. The registry access stays over there; the
/// decision - which is the part that got this wrong twice - is here where it can be
/// tested.
/// </para>
/// </summary>
public static class AutoStartPolicy
{
    /// <summary>
    /// Whether the Run entry should be rewritten to point at the running executable.
    /// <para>
    /// Only two things justify it: there is no entry at all (the installer never
    /// ran, or something removed it), or the entry names a program that is no longer
    /// on disk (the exe was moved, and sign-in launch is silently broken).
    /// </para>
    /// <para>
    /// "It points at a different copy that is still there" is explicitly not one of
    /// them. <c>StartWithWindows</c> is stored in the shared
    /// <c>%LOCALAPPDATA%</c> settings file, so it is true for every copy of the exe
    /// on the machine: repointing on mismatch meant running the dev build
    /// (<c>bin\Debug\net10.0-windows\RoSToolsSidecar.exe</c>) once aimed autostart at
    /// the build output, and the next <c>dotnet clean</c> or branch switch broke
    /// sign-in launch - the exact failure the reassert was written to prevent, now
    /// caused by it. A copy run once out of <c>Downloads</c> or an extracted zip does
    /// the same. The installed copy is the one the installer wrote, and a second copy
    /// running has no standing to overrule it.
    /// </para>
    /// </summary>
    /// <param name="storedCommand">
    /// The raw Run value, which is a command line rather than a path, or null when
    /// there is no value (or it could not be read - which is handled the same way,
    /// since an entry nothing can read is an entry nothing can trust).
    /// </param>
    /// <param name="exists">
    /// How to ask whether a path is on disk. Defaults to <see cref="File.Exists"/>;
    /// the tests pass their own.
    /// </param>
    public static bool ShouldRepair(string? storedCommand, Func<string, bool>? exists = null)
    {
        var stored = ExecutablePathOf(storedCommand);
        if (stored is null)
        {
            return true;
        }

        exists ??= File.Exists;

        try
        {
            // Present and on disk: another copy owns the entry, and it works. Leave it.
            return !exists(stored);
        }
        catch (Exception ex)
        {
            // A hand-edited value that is not a usable path launches nothing at
            // sign-in, so it is worth replacing.
            Log.Warn($"could not check the Run entry's target: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// The executable out of a Run value, which is a command line rather than a path.
    /// A quoted program is taken up to its closing quote so arguments are dropped;
    /// an unquoted one is taken whole, because a bare
    /// <c>C:\Program Files\...\RoSToolsSidecar.exe</c> is one path, not two tokens.
    /// </summary>
    public static string? ExecutablePathOf(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();

        if (value[0] == '"')
        {
            var close = value.IndexOf('"', 1);
            value = close > 1 ? value[1..close] : value.Trim('"');
        }

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }
}
