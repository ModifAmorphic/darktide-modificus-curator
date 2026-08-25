using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// <see cref="ModCleanup.PruneUnreferenced"/> behavior: a referenced linked
/// container survives the startup prune (kept by containerId reference, not
/// version), an unreferenced one is pruned, and the external target is never
/// touched in either case; and managed containers keep every folder a
/// profile would stage PLUS the container's current latest (a pinned entry
/// must not let the prune delete the newest version), while unreferenced
/// superseded versions are still reclaimed.
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

    // ---- managed containers: the policy folder + the current latest survive ----

    /// <summary>
    /// Seeds a mixed manual/download container: "hand" (a manual import)
    /// arrives first, then the June-published download, then the
    /// March-published download. Under the arrival rule the latest is the
    /// June download (the newest arrival is a download, so the newest
    /// downloaded version by publish date governs). Returns the container
    /// plus each version's folder.
    /// </summary>
    private static (ModContainer Container, string Hand, string June, string March)
        SeedMixedContainer(ProfileServiceFixture fx)
    {
        var june = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var march = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");

        fx.Repo.AddVersion(container.Id, "hand", EmptyPopulate);
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, june);
        var seeded = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, march);
        return (
            seeded,
            seeded.Versions.Single(v => v.VersionString == "hand").Folder,
            seeded.Versions.Single(v => v.VersionString == "1.0").Folder,
            seeded.Versions.Single(v => v.VersionString == "2.0").Folder);
    }

    private static readonly Action<string> EmptyPopulate = dir =>
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
    };

    private static void AssertVersionExists(ProfileServiceFixture fx, ModContainer container, string folder)
    {
        Assert.Contains(container.Versions, v => v.Folder == folder);
        Assert.True(Directory.Exists(fx.Repo.GetVersionFolderPath(container.Id, folder)));
    }

    private static void AssertVersionGone(ProfileServiceFixture fx, ModContainer container, string folder)
    {
        Assert.DoesNotContain(container.Versions, v => v.Folder == folder);
        Assert.False(Directory.Exists(fx.Repo.GetVersionFolderPath(container.Id, folder)));
    }

    [Fact]
    public void A_pinned_entry_keeps_the_pinned_folder_and_the_container_latest()
    {
        // The sibling hole: a PinnedPolicy entry resolves only the pinned
        // folder, which used to let the prune delete the container's newest
        // version on restart. The latest now survives unconditionally, so
        // both the pinned March download + the latest June download stay;
        // only the unreferenced manual (not pinned, not latest) is dropped.
        using var fx = new ProfileServiceFixture();
        var (container, hand, june, march) = SeedMixedContainer(fx);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, container.Id, new PinnedPolicy(march));

        ModCleanup.PruneUnreferenced(fx.Service, fx.Repo);

        var reloaded = fx.Repo.Get(container.Id);
        Assert.NotNull(reloaded);
        AssertVersionExists(fx, reloaded!, march); // the pin
        AssertVersionExists(fx, reloaded!, june);  // the container's latest
        AssertVersionGone(fx, reloaded!, hand);    // superseded + unreferenced
    }

    [Fact]
    public void A_latest_entry_on_a_mixed_container_keeps_the_policy_folder_and_the_latest()
    {
        // The LatestPolicy resolves the June download (also the container's
        // latest); the manual that arrived first is neither policy-resolved
        // nor latest, so the GC still reclaims it.
        using var fx = new ProfileServiceFixture();
        var (container, hand, june, _) = SeedMixedContainer(fx);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        ModCleanup.PruneUnreferenced(fx.Service, fx.Repo);

        var reloaded = fx.Repo.Get(container.Id);
        Assert.NotNull(reloaded);
        AssertVersionExists(fx, reloaded!, june);
        AssertVersionGone(fx, reloaded!, hand);
    }

    [Fact]
    public void An_unreferenced_managed_container_is_pruned_entirely()
    {
        // Empty-container removal is unchanged: a container no profile
        // references is dropped, manifest + directory.
        using var fx = new ProfileServiceFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "Orphan");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        // No profile references it.

        ModCleanup.PruneUnreferenced(fx.Service, fx.Repo);

        Assert.Null(fx.Repo.Get(container.Id));
        Assert.False(Directory.Exists(fx.ContainerDir(container.Id)));
    }
}
