using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Cross-cutting sentinel-safety tests for linked mods: a marker file inside
/// the external target survives every Curator operation (the external target is
/// never modified/deleted/copied-from), and the availability signal recomputes
/// on rescan (missing-then-returned). Uses the real
/// <see cref="IModRepository"/> + <see cref="IModImportService"/> via DI
/// (black-box).
/// </summary>
public sealed class LinkedFolderSafetyTests
{
    // ---- availability recomputation on rescan --------------------------------

    [Fact]
    public void Availability_flips_false_when_external_folder_is_missing_then_true_when_it_returns()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var containerId = fx.Imports.LinkFolder(external);

        Assert.True(fx.Repo.IsExternalAvailable(containerId));

        // Remove the external target + rescan: availability flips to false.
        Directory.Delete(external, recursive: true);
        fx.Repo.Rescan();
        Assert.False(fx.Repo.IsExternalAvailable(containerId));

        // Recreate it + rescan: availability flips back.
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "LinkedMod.mod"), "LinkedMod");
        fx.Repo.Rescan();
        Assert.True(fx.Repo.IsExternalAvailable(containerId));
    }

    [Fact]
    public void Rescan_does_not_touch_the_external_target()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var containerId = fx.Imports.LinkFolder(external);

        fx.Repo.Rescan();
        fx.Repo.Rescan();

        Assert.NotNull(fx.Repo.Get(containerId));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    // ---- profile lifecycle sentinel survival --------------------------------

    [Fact]
    public void Sentinel_survives_link_stage_remove_rescan_and_profile_deletion_sequence()
    {
        // An end-to-end sequence exercising every Curator operation against the
        // same external target; the sentinel must be byte-identical at the end.
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("Survivor");
        var sentinel = Path.Combine(external, "sentinel.txt");

        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        fx.Service.SetModEnabled(profile.Id, containerId, enabled: false);
        fx.Service.PrepareModRoot(profile.Id);
        fx.Service.SetModEnabled(profile.Id, containerId, enabled: true);
        fx.Service.SetModPolicy(profile.Id, containerId, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        fx.Repo.Rescan();
        fx.Service.RemoveMod(profile.Id, containerId);
        fx.Service.DeleteProfile(profile.Id);

        Assert.True(Directory.Exists(external));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }
}
