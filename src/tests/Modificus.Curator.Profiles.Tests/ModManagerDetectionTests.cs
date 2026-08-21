using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// The alternate mod-manager derivation on
/// <see cref="IProfileService.GetActiveModManager"/>: an enabled profile mod
/// whose resolved staging target is a <c>base</c> folder containing
/// <c>mod_manager.lua</c> (the Darktide Mod Loader family convention;
/// AML, Nexus mod 246, is the known occupant). Detection is content-based +
/// manager-agnostic: no Nexus id is consulted, the <c>base.mod</c> descriptor
/// plays no part, and staging + <c>mods.lst</c> keep their ordinary behavior
/// for the manager mod.
/// </summary>
public sealed class ModManagerDetectionTests
{
    /// <summary>
    /// Creates a container + version whose base folder is <paramref name="baseFolderName"/>
    /// holding <paramref name="managerFileName"/> (when non-null) +
    /// <c>base.mod</c> (a hand-shaped on-disk structure; AddVersion's populate
    /// callback does no import validation, so the shape is expressible).
    /// </summary>
    private static ModContainer AddContainerWithBaseFolder(
        ProfileServiceFixture fx, string name, string baseFolderName, string? managerFileName, ModSource? source = null)
    {
        var container = fx.Repo.CreateContainer(source ?? new UntrackedSource(), name);
        return fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            var baseDir = Path.Combine(dir, baseFolderName);
            Directory.CreateDirectory(baseDir);
            if (managerFileName is not null)
            {
                File.WriteAllText(Path.Combine(baseDir, managerFileName), "-- manager");
            }
            File.WriteAllText(Path.Combine(baseDir, baseFolderName + ".mod"), string.Empty);
            File.WriteAllText(Path.Combine(baseDir, "marker.txt"), name);
        });
    }

    // ---- recognition ---------------------------------------------------------

    [Fact]
    public void Enabled_base_folder_with_mod_manager_lua_is_the_active_manager()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var manager = AddContainerWithBaseFolder(fx, "Alternate Mod Loader", "base", "mod_manager.lua");
        fx.Service.AddMod(profile.Id, manager.Id, ModVersionPolicy.Latest);

        var active = fx.Service.GetActiveModManager(profile.Id);

        Assert.NotNull(active);
        Assert.Equal(manager.Id, active!.ContainerId);
        Assert.Equal(
            Path.Combine(fx.StagedDir(profile.Id), "mods", "base", "mod_manager.lua"),
            active.ManagerPath);
    }

    [Fact]
    public void Manager_mod_stages_and_lists_ordinarily()
    {
        // Staging + mods.lst are UNCHANGED by detection: the manager mod is an
        // ordinary staged mod (link + mods.lst entry), and the flag emission
        // is the only special behavior.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var manager = AddContainerWithBaseFolder(fx, "Alternate Mod Loader", "base", "mod_manager.lua");
        fx.Service.AddMod(profile.Id, manager.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "base")));
        Assert.True(File.Exists(Path.Combine(fx.StagedModLink(profile.Id, "base"), "mod_manager.lua")));
        Assert.Contains("base", File.ReadAllLines(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Nexus_sourced_manager_content_is_recognized_the_same()
    {
        // Detection is source-agnostic: a Nexus mod 246 container with the
        // same content derives identically (and proves no id special-casing
        // is needed either way).
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var manager = AddContainerWithBaseFolder(
            fx, "AML", "base", "mod_manager.lua", source: new NexusSource { ModId = 246 });
        fx.Service.AddMod(profile.Id, manager.Id, ModVersionPolicy.Latest);

        var active = fx.Service.GetActiveModManager(profile.Id);

        Assert.NotNull(active);
        Assert.Equal(manager.Id, active!.ContainerId);
    }

    [Fact]
    public void Linked_external_base_folder_with_mod_manager_lua_is_recognized()
    {
        // A linked external folder named base stages from its own location,
        // so the linked resolver sees the same shape; the returned path is
        // the STAGED link path, not the external one.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var external = fx.MakeExternalModFolder("base");
        File.WriteAllText(Path.Combine(external, "mod_manager.lua"), "-- manager");
        var linkedId = fx.Imports.LinkFolder(external);
        fx.Service.AddMod(profile.Id, linkedId, ModVersionPolicy.Latest);

        var active = fx.Service.GetActiveModManager(profile.Id);

        Assert.NotNull(active);
        Assert.Equal(linkedId, active!.ContainerId);
        Assert.Equal(
            Path.Combine(fx.StagedDir(profile.Id), "mods", "base", "mod_manager.lua"),
            active.ManagerPath);
    }

    // ---- non-recognition -----------------------------------------------------

    [Fact]
    public void Disabled_manager_mod_yields_null_and_stages_nothing()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var manager = AddContainerWithBaseFolder(fx, "Alternate Mod Loader", "base", "mod_manager.lua");
        fx.Service.AddMod(profile.Id, manager.Id, ModVersionPolicy.Latest);
        fx.Service.SetModEnabled(profile.Id, manager.Id, enabled: false);

        Assert.Null(fx.Service.GetActiveModManager(profile.Id));

        fx.Service.PrepareModRoot(profile.Id);
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "base")));
        Assert.DoesNotContain("base", File.ReadAllLines(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Unresolvable_manager_mod_yields_null()
    {
        // The version folder vanishing (a hand-delete) makes the entry
        // unresolvable, so no manager is derived (never a path to a missing
        // file: Relay hard-refuses a configured-but-missing manager).
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var manager = AddContainerWithBaseFolder(fx, "Alternate Mod Loader", "base", "mod_manager.lua");
        fx.Service.AddMod(profile.Id, manager.Id, ModVersionPolicy.Latest);
        Directory.Delete(fx.VersionDir(manager.Id, manager.Versions[0].Folder), recursive: true);

        Assert.Null(fx.Service.GetActiveModManager(profile.Id));
    }

    [Fact]
    public void Base_folder_without_mod_manager_lua_yields_null_but_stages_ordinarily()
    {
        // An ordinary mod that happens to occupy the base folder: no manager
        // file, no derivation, but staging + mods.lst treat it like any mod.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var ordinary = AddContainerWithBaseFolder(fx, "Ordinary", "base", managerFileName: null);
        fx.Service.AddMod(profile.Id, ordinary.Id, ModVersionPolicy.Latest);

        Assert.Null(fx.Service.GetActiveModManager(profile.Id));

        fx.Service.PrepareModRoot(profile.Id);
        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "base")));
        Assert.Contains("base", File.ReadAllLines(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Capitalized_Base_folder_is_not_the_manager_base()
    {
        // The base folder name is an ordinal lower-case literal (mirroring the
        // dmf convention): "Base" is an ordinary mod folder.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var lookalike = AddContainerWithBaseFolder(fx, "Lookalike", "Base", "mod_manager.lua");
        fx.Service.AddMod(profile.Id, lookalike.Id, ModVersionPolicy.Latest);

        Assert.Null(fx.Service.GetActiveModManager(profile.Id));
    }

    [Fact]
    public void Unknown_profile_id_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() => fx.Service.GetActiveModManager(Guid.NewGuid()));
    }

    // ---- first-in-order defense ----------------------------------------------

    [Fact]
    public void Two_base_entries_derive_the_first_in_order()
    {
        // The import-time base-name collision block makes two base mods
        // unreachable through normal flows; AddMod (a programmatic surface)
        // can still produce them, so first-in-order-wins is the documented
        // defense rather than an unspecified outcome.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var first = AddContainerWithBaseFolder(fx, "First Manager", "base", "mod_manager.lua");
        var second = AddContainerWithBaseFolder(fx, "Second Manager", "base", "mod_manager.lua");
        fx.Service.AddMod(profile.Id, first.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, second.Id, ModVersionPolicy.Latest);

        var active = fx.Service.GetActiveModManager(profile.Id);

        Assert.NotNull(active);
        Assert.Equal(first.Id, active!.ContainerId);
    }
}
