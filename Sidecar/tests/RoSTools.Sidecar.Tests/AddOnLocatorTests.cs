using RoSTools.Sidecar.Core;
using Xunit;

namespace RoSTools.Sidecar.Tests;

public class AddOnLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rostools-locator-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_folder_with_the_toc_is_the_addon()
    {
        var addOn = Path.Combine(_root, "Interface", "AddOns", "RoS-Tools");
        Directory.CreateDirectory(addOn);
        File.WriteAllText(Path.Combine(addOn, AddOnLocator.TocFileName), "## Interface: 120000");

        Assert.True(AddOnLocator.LooksLikeAddOnFolder(addOn));
    }

    [Fact]
    public void The_AddOns_parent_is_not_the_addon()
    {
        // The mistake this guard exists for: a user browses to Interface\AddOns,
        // the sidecar writes GuildData.lua there, and the addon never reads it.
        var addOns = Path.Combine(_root, "Interface", "AddOns");
        Directory.CreateDirectory(Path.Combine(addOns, "RoS-Tools"));
        File.WriteAllText(
            Path.Combine(addOns, "RoS-Tools", AddOnLocator.TocFileName), "## Interface: 120000");

        Assert.False(AddOnLocator.LooksLikeAddOnFolder(addOns));
    }

    [Fact]
    public void A_folder_without_the_toc_is_rejected()
    {
        Directory.CreateDirectory(_root);
        Assert.False(AddOnLocator.LooksLikeAddOnFolder(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_paths_are_rejected(string? path) =>
        Assert.False(AddOnLocator.LooksLikeAddOnFolder(path));

    [Fact]
    public void The_data_file_is_the_only_path_written()
    {
        var addOn = Path.Combine(_root, "RoS-Tools");
        var expected = Path.Combine(addOn, "Data", "GuildData.lua");

        Assert.Equal(expected, AddOnLocator.DataFileFor(addOn));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // A leftover temp folder is not worth failing a test run over.
        }
    }
}
