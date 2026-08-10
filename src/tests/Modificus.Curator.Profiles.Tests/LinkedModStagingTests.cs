using Modificus.Curator.Mods;
using Modificus.Curator.General;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Linked-mod staging + collision behavior (the
/// <see cref="IModImportService.LinkFolder"/> flow staged through
/// <see cref="IProfileService.PrepareModRoot"/>). Covers: linking creates no
/// copy; staging links <c>staged/mods/&lt;baseName&gt;</c> directly to the external
/// folder; a missing external folder at stage time is skipped (no fallback copy);
/// the external target's sentinel survives staging, enable/disable, reorder,
/// remove, and profile deletion; and the cross-source base-name collision hard-
/// block covers a linked container whose folder name matches an existing managed
/// mod's base name in the same profile.
/// </summary>
public sealed class LinkedModStagingTests
{
    // ---- staging links directly to the external folder ---------------------

    [Fact]
    public void Linked_mod_stages_directly_from_the_external_folder_no_copy()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        var link = fx.StagedModLink(profile.Id, "LinkedMod");
        Assert.True(Directory.Exists(link), "staged entry should resolve to the external folder");
        Assert.True(IsSymlink(link), "staged entry should be a staging link, not a copy");
        Assert.Equal(external, ResolveLink(link));

        // mods.lst lists the linked base name.
        Assert.Contains("LinkedMod", File.ReadAllLines(fx.ModsLst(profile.Id)));

        // No copy anywhere under the mods root beyond the container's manifest.
        var containerDir = fx.ContainerDir(containerId);
        var entries = Directory.GetFileSystemEntries(containerDir);
        Assert.Single(entries);
        Assert.Equal("container.json", Path.GetFileName(entries[0]));
    }

    [Fact]
    public void Linked_mod_staging_leaves_the_external_sentinel_untouched()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        Assert.True(File.Exists(sentinel));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    // ---- missing external folder at stage time -----------------------------

    [Fact]
    public void Missing_external_folder_at_stage_time_is_skipped_with_no_fallback_copy()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("Gone");
        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);

        // Remove the external target before staging.
        Directory.Delete(external, recursive: true);

        fx.Service.PrepareModRoot(profile.Id);

        // No staged entry, no mods.lst entry, and NO copy under the mods root.
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "Gone")));
        var lst = File.Exists(fx.ModsLst(profile.Id))
            ? File.ReadAllLines(fx.ModsLst(profile.Id))
            : Array.Empty<string>();
        Assert.DoesNotContain("Gone", lst);
        // The container still exists (Curator does not auto-remove it); staging
        // just skipped it. The cached availability flips to false on a rescan
        // (the staging skip itself re-checks Directory.Exists independently).
        Assert.NotNull(fx.Repo.Get(containerId));
        fx.Repo.Rescan();
        Assert.False(fx.Repo.IsExternalAvailable(containerId));
    }

    [Fact]
    public void Missing_external_folder_does_not_block_other_mods_from_staging()
    {
        using var fx = new ProfileServiceFixture();
        var goneExternal = fx.MakeExternalModFolder("Gone");
        var goneId = fx.Imports.LinkFolder(goneExternal);
        var okExternal = fx.MakeExternalModFolder("Ok");
        var okId = fx.Imports.LinkFolder(okExternal);
        Directory.Delete(goneExternal, recursive: true);

        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, goneId, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, okId, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        // Gone skipped; Ok staged.
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "Gone")));
        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "Ok")));
        Assert.Equal(new[] { "Ok" }, File.ReadAllLines(fx.ModsLst(profile.Id)));
    }

    // ---- enable/disable + reorder ------------------------------------------

    [Fact]
    public void Linked_mod_can_be_disabled_and_reenabled_and_sentinel_survives()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);

        // Disable: not staged.
        fx.Service.SetModEnabled(profile.Id, containerId, enabled: false);
        fx.Service.PrepareModRoot(profile.Id);
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "LinkedMod")));

        // Re-enable: staged again, sentinel intact.
        fx.Service.SetModEnabled(profile.Id, containerId, enabled: true);
        fx.Service.PrepareModRoot(profile.Id);
        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "LinkedMod")));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Linked_mod_load_order_is_respected_and_sentinel_survives_reorder()
    {
        using var fx = new ProfileServiceFixture();
        var extA = fx.MakeExternalModFolder("LinkedA");
        var extB = fx.MakeExternalModFolder("LinkedB");
        var idA = fx.Imports.LinkFolder(extA);
        var idB = fx.Imports.LinkFolder(extB);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, idA, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, idB, ModVersionPolicy.Latest);

        // Reverse the order.
        fx.Service.SetModOrder(profile.Id, new[] { idB, idA });
        fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal(new[] { "LinkedB", "LinkedA" }, File.ReadAllLines(fx.ModsLst(profile.Id)));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(extA, "sentinel.txt")));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(extB, "sentinel.txt")));
    }

    // ---- remove from profile + profile deletion ----------------------------

    [Fact]
    public void Removing_a_linked_mod_from_the_profile_leaves_the_external_target_intact()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);

        fx.Service.RemoveMod(profile.Id, containerId);
        fx.Service.PrepareModRoot(profile.Id);

        // Not staged.
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "LinkedMod")));
        // The container is still in the repository (the startup prune reclaims
        // it when no profile references it; removal is not a delete).
        Assert.NotNull(fx.Repo.Get(containerId));
        // External target + sentinel survive.
        Assert.True(Directory.Exists(external));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Deleting_the_profile_leaves_the_external_target_intact()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);

        fx.Service.DeleteProfile(profile.Id);

        Assert.False(Directory.Exists(fx.ProfileDir(profile.Id)));
        Assert.True(Directory.Exists(external));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    // ---- cross-source base-name collision hard-block -----------------------

    [Fact]
    public void Linked_base_name_colliding_with_a_managed_mod_in_the_profile_is_reported()
    {
        // A linked container whose folder name equals an existing managed mod's
        // base name in the same profile is a collision (the loader cannot tell
        // two staged/mods/<baseName> links apart). GetBaseNameCollision resolves the
        // linked base name via the shared ResolveStagingTarget path.
        using var fx = new ProfileServiceFixture();
        // Managed mod with base name "Shared" (the fixture sanitizes the name).
        var managed = fx.AddContainerWithVersion("Shared");
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, managed.Id, ModVersionPolicy.Latest);

        // A linked external folder with the SAME base name "Shared".
        var external = fx.MakeExternalModFolder("Shared");
        var linkedId = fx.Imports.LinkFolder(external);

        var collision = fx.Service.GetBaseNameCollision(profile.Id, "Shared", excludeContainerId: null);

        Assert.NotNull(collision);
        // The collision is one of the two containers (the managed one is already
        // in the profile; the linked one is not yet added).
        Assert.True(collision!.ContainerId == managed.Id || collision.ContainerId == linkedId);
    }

    [Fact]
    public void Linked_base_name_colliding_with_a_disabled_managed_mod_in_the_profile_is_reported()
    {
        // GetBaseNameCollision considers ALL mods (enabled + disabled): a
        // disabled colliding mod could be enabled later. This pins that behavior
        // for the linked cross-source path specifically.
        using var fx = new ProfileServiceFixture();
        // Managed mod with base name "Shared" (the fixture sanitizes the name).
        var managed = fx.AddContainerWithVersion("Shared");
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, managed.Id, ModVersionPolicy.Latest);

        // A linked external folder with the SAME base name "Shared".
        var external = fx.MakeExternalModFolder("Shared");
        var linkedId = fx.Imports.LinkFolder(external);

        // Disable the managed entry: still a collision (a disabled colliding mod
        // could be enabled later).
        fx.Service.SetModEnabled(profile.Id, managed.Id, enabled: false);

        var collision = fx.Service.GetBaseNameCollision(profile.Id, "Shared", excludeContainerId: null);

        Assert.NotNull(collision);
        // The collision is one of the two containers (the managed one is already
        // in the profile, disabled; the linked one is not yet added).
        Assert.True(collision!.ContainerId == managed.Id || collision.ContainerId == linkedId);
    }

    [Fact]
    public void Linked_re_add_of_a_container_already_in_the_profile_is_not_a_collision()
    {
        // Mirrors the managed-mod behavior: excludeContainerId skips a re-add
        // (AddMod is idempotent on containerId), so it is not a collision.
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var linkedId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        fx.Service.AddMod(profile.Id, linkedId, ModVersionPolicy.Latest);

        var collision = fx.Service.GetBaseNameCollision(
            profile.Id, "LinkedMod", excludeContainerId: linkedId);

        Assert.Null(collision);
    }

    // ---- helpers ------------------------------------------------------------

    private static bool IsSymlink(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string ResolveLink(string path) =>
        File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
            ?? throw new IOException("not a link");
}
