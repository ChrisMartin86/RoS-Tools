using RoSTools.Sidecar.Core;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// The decision behind <c>AutoStart.Reassert</c>. It lives in Core so it can be
/// tested at all: <c>AutoStart</c> itself is in the <c>net10.0-windows</c>
/// executable, which this assembly cannot reference, and the registry half of it is
/// three lines around this call.
/// <para>
/// The property under test: startup may repair a Run entry that would launch
/// nothing, and may not touch one that works. Repointing on mismatch is what made
/// running the dev build once break sign-in launch at the next clean.
/// </para>
/// </summary>
public class AutoStartPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-autostart-" + Guid.NewGuid().ToString("N"));

    private readonly string _installed;

    public AutoStartPolicyTests()
    {
        Directory.CreateDirectory(_root);
        Log.DirectoryOverride = Path.Combine(_root, "logs");

        _installed = Path.Combine(_root, "RoSToolsSidecar.exe");
        File.WriteAllText(_installed, "MZ");
    }

    [Fact]
    public void An_absent_entry_is_written()
    {
        // The installer never ran, or something removed the value. Nothing launches
        // at sign-in and the box says it should.
        Assert.True(AutoStartPolicy.ShouldRepair(null, File.Exists));
        Assert.True(AutoStartPolicy.ShouldRepair(string.Empty, File.Exists));
        Assert.True(AutoStartPolicy.ShouldRepair("   ", File.Exists));
    }

    [Fact]
    public void An_entry_pointing_at_a_program_that_is_gone_is_rewritten()
    {
        // The exe was moved by anything that is not Install-Sidecar.ps1 - a manual
        // copy, a profile move, a restore from backup. Windows launches nothing and
        // says nothing, which is the failure this repair exists for.
        var moved = Path.Combine(_root, "old-location", "RoSToolsSidecar.exe");

        Assert.True(AutoStartPolicy.ShouldRepair($"\"{moved}\"", File.Exists));
    }

    [Fact]
    public void An_entry_pointing_somewhere_else_that_still_exists_is_left_alone()
    {
        // The one that mattered. StartWithWindows lives in the shared %LOCALAPPDATA%
        // settings file, so it is true for *every* copy of the exe on the machine: a
        // maintainer running bin\Debug\net10.0-windows\RoSToolsSidecar.exe once
        // repointed autostart at the build output, and the next dotnet clean or
        // branch switch silently broke sign-in launch. A copy run from Downloads or
        // an extracted zip that is later deleted does exactly the same.
        //
        // The installed copy exists and is what the installer chose. A second copy
        // has no standing to overrule it.
        Assert.False(AutoStartPolicy.ShouldRepair($"\"{_installed}\"", File.Exists));
    }

    [Fact]
    public void Arguments_after_a_quoted_program_are_not_part_of_the_path()
    {
        Assert.False(AutoStartPolicy.ShouldRepair($"\"{_installed}\" --minimised", File.Exists));
    }

    [Fact]
    public void An_unquoted_path_with_spaces_is_one_path_and_not_two_tokens()
    {
        var spaced = Path.Combine(_root, "Program Files", "RoSToolsSidecar.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(spaced)!);
        File.WriteAllText(spaced, "MZ");

        Assert.False(AutoStartPolicy.ShouldRepair(spaced, File.Exists));
    }

    [Fact]
    public void A_value_that_is_not_a_usable_path_is_rewritten()
    {
        // Hand-edited, or a leftover from something else entirely. It launches
        // nothing, so replacing it costs the user nothing either.
        Assert.True(AutoStartPolicy.ShouldRepair(
            "\"C:\\nowhere\\at\\all.exe\"",
            static path => throw new ArgumentException($"not a path: {path}")));
    }

    [Fact]
    public void The_check_is_the_stored_path_and_never_the_running_one()
    {
        // Pinning the shape of the decision, not just its outcome: nothing about the
        // running executable may enter into it. Reintroducing that comparison brings
        // the dev-build failure straight back.
        var asked = new List<string>();

        AutoStartPolicy.ShouldRepair($"\"{_installed}\"", path =>
        {
            asked.Add(path);
            return File.Exists(path);
        });

        Assert.Equal([_installed], asked);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Log.DirectoryOverride = null;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
