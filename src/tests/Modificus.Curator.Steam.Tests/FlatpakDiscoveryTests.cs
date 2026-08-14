namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Flatpak Steam detection: when the resolved Steam install is the Flatpak
/// candidate, a non-fatal warning surfaces (the UI can flag it; some Steam
/// integrations are limited under Flatpak).
/// </summary>
public sealed class FlatpakDiscoveryTests
{
    [Fact]
    public void Flatpak_root_resolving_emits_flatpak_warning()
    {
        using var fx = new SteamFixture();
        // Only the Flatpak root is a valid Steam install.
        fx.WithLibraryFoldersAtFlatpakRoot();
        fx.WithDarktide(fx.FlatpakRoot);
        fx.WithCompatdata(fx.FlatpakRoot);
        fx.WithCompatToolMapping(fx.FlatpakRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.FlatpakRoot, result.SteamInstallPath);
        Assert.Contains(result.Warnings, w => w.Contains("Flatpak", StringComparison.Ordinal));
    }

    [Fact]
    public void Native_root_resolving_emits_no_flatpak_warning()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithCompatToolMapping(fx.SteamRoot, "GE-Proton9-3");
        fx.WithCustomProtonTool(fx.CompatToolsDir, "GE-Proton9-3");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Flatpak", StringComparison.Ordinal));
    }
}
