using RoSTools.Sidecar.Core;
using Xunit;

namespace RoSTools.Sidecar.Tests;

public class DataInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-installer-" + Guid.NewGuid().ToString("N"));

    public DataInstallerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Creates_the_Data_folder_when_it_is_missing()
    {
        var staging = Stage("new roster");
        var destination = Path.Combine(_root, "RoS-Tools", "Data", "GuildData.lua");

        DataInstaller.Install(staging, destination);

        Assert.Equal("new roster", File.ReadAllText(destination));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public void Keeps_one_rollback_copy_of_the_previous_roster()
    {
        var destination = Path.Combine(_root, "GuildData.lua");
        File.WriteAllText(destination, "old roster");

        DataInstaller.Install(Stage("new roster"), destination);

        Assert.Equal("new roster", File.ReadAllText(destination));
        Assert.Equal("old roster", File.ReadAllText(destination + ".bak"));
    }

    [Fact]
    public void Replacing_twice_leaves_the_bak_one_generation_behind()
    {
        var destination = Path.Combine(_root, "GuildData.lua");

        DataInstaller.Install(Stage("first"), destination);
        DataInstaller.Install(Stage("second"), destination);

        Assert.Equal("second", File.ReadAllText(destination));
        Assert.Equal("first", File.ReadAllText(destination + ".bak"));
    }

    private string Stage(string content)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".staging");
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

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
