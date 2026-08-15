using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Cross-cutting sentinel-safety tests for linked mods: a marker file inside
/// the external target survives every Curator operation (the external target is
/// never modified/deleted/copied-from). Uses the real
/// <see cref="IModRepository"/> + <see cref="IModImportService"/> via DI
/// (black-box). The availability signal's index-rebuild recomputation is
/// covered by the Mods-layer repository tests.
/// </summary>
public sealed class LinkedFolderSafetyTests
{
    // ---- profile lifecycle sentinel survival --------------------------------

    [Fact]
    public void Sentinel_survives_link_stage_remove_and_profile_deletion_sequence()
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
        fx.Service.RemoveMod(profile.Id, containerId);
        fx.Service.DeleteProfile(profile.Id);

        Assert.True(Directory.Exists(external));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }
}
