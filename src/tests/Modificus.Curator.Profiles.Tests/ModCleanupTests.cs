using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// <see cref="ModCleanup.PruneUnreferenced"/> linked-container behavior: a
/// referenced linked container survives the startup prune (kept by containerId
/// reference, not version), an unreferenced one is pruned, and the external
/// target is never touched in either case. Covers the sentinel-based safety
/// invariant across the prune + the missing-then-returned availability flip.
/// </summary>
public sealed class ModCleanupTests
{
    [Fact]
    public void Referenced_linked_container_survives_the_startup_prune()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);

        ModCleanup.PruneUnreferenced(fx.Service, fx.Repo);

        // The linked container (zero versions, but referenced) survives.
        Assert.NotNull(fx.Repo.Get(containerId));
        Assert.True(Directory.Exists(fx.ContainerDir(containerId)));
        // External target + sentinel untouched.
        Assert.True(Directory.Exists(external));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Disabled_linked_entry_still_counts_as_referenced_for_prune()
    {
        // Mirrors managed behavior: enable/disable is a stage-time decision,
        // not a delete signal. A disabled linked entry still references its
        // container so the prune keeps it.
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);
        fx.Service.SetModEnabled(profile.Id, containerId, enabled: false);

        ModCleanup.PruneUnreferenced(fx.Service, fx.Repo);

        Assert.NotNull(fx.Repo.Get(containerId));
    }

    [Fact]
    public void Unreferenced_linked_container_is_pruned_and_external_target_survives()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var containerId = fx.Imports.LinkFolder(external);
        // No profile references it.

        ModCleanup.PruneUnreferenced(fx.Service, fx.Repo);

        // Container pruned (manifest + dir).
        Assert.Null(fx.Repo.Get(containerId));
        Assert.False(Directory.Exists(fx.ContainerDir(containerId)));
        // External target + sentinel untouched: the prune removed only the
        // container's mods-root footprint (its container.json dir).
        Assert.True(Directory.Exists(external));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Removing_a_linked_mod_from_the_only_profile_then_pruning_drops_the_container()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);

        fx.Service.RemoveMod(profile.Id, containerId);

        ModCleanup.PruneUnreferenced(fx.Service, fx.Repo);

        Assert.Null(fx.Repo.Get(containerId));
        Assert.True(Directory.Exists(external)); // external target untouched
    }

    [Fact]
    public void Linked_container_referenced_by_a_second_profile_survives_when_the_first_drops_it()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var containerId = fx.Imports.LinkFolder(external);
        var profileA = fx.Service.CreateProfile("A", string.Empty, new LaunchSettings());
        var profileB = fx.Service.CreateProfile("B", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profileA.Id, containerId, ModVersionPolicy.Latest);
        fx.Service.AddMod(profileB.Id, containerId, ModVersionPolicy.Latest);

        fx.Service.RemoveMod(profileA.Id, containerId);

        ModCleanup.PruneUnreferenced(fx.Service, fx.Repo);

        // Still referenced by profile B -> kept.
        Assert.NotNull(fx.Repo.Get(containerId));
    }

    [Fact]
    public void Deleting_a_profile_then_pruning_drops_its_linked_container_when_no_other_profile_uses_it()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);

        fx.Service.DeleteProfile(profile.Id);

        ModCleanup.PruneUnreferenced(fx.Service, fx.Repo);

        Assert.Null(fx.Repo.Get(containerId));
        Assert.True(Directory.Exists(external));
    }
}
