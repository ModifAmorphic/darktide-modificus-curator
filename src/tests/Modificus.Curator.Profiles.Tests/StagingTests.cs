using System.Text;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Container-based staging contract: each enabled mod resolves its
/// <see cref="ModVersionPolicy"/> against its <see cref="ModContainer"/>
/// (<see cref="LatestPolicy"/> -> the container's isLatest version folder;
/// <see cref="PinnedPolicy"/> -> the version whose <see cref="ModVersion.Folder"/>
/// matches the pin's <see cref="PinnedPolicy.VersionId"/>) and is linked into
/// <c>staged/mods/&lt;baseName&gt;</c>, where the base name is discovered on the fly as
/// the single subdirectory inside the version folder (the mod's base folder);
/// missing containers/versions and corrupted version folders (zero/multiple
/// subdirs) are skipped + warned; same-base-name collisions are blocked at import
/// time (<see cref="IProfileService.GetBaseNameCollision"/>), so staging is a
/// simple loop with no dedupe; the staged <c>mods/</c> dir holds only staging
/// links (an NTFS junction on Windows, a symlink on Linux) + <c>mods.lst</c>
/// (no copied files); a staging-link-creation failure throws
/// <see cref="IOException"/> (never silently copies).
/// </summary>
public sealed class StagingTests
{
    // ---- Latest / Pinned symlink targets -----------------------------------

    [Fact]
    public void Latest_policy_symlinks_to_the_isLatest_version_folder()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        var link = fx.StagedModLink(profile.Id, "DMF");
        Assert.True(Directory.Exists(link), "staged symlink should resolve to the base folder");
        Assert.True(IsSymlink(link), "staged entry should be a symlink, not a copy");
        Assert.Equal(
            Path.Combine(fx.VersionDir(container.Id, container.Versions[0].Folder), "DMF"),
            ResolveLink(link));
    }

    [Fact]
    public void Pinned_policy_symlinks_to_the_version_matching_the_pin()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        // Add v1.0 (becomes isLatest), then v2.0 (becomes isLatest). Pin to 1.0
        // by its version id (the ModVersion.Folder value).
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        fx.AddVersion(container.Id, "2.0");
        var v1Folder = container.Versions.Single(v => v.VersionString == "1.0").Folder;
        fx.Service.AddMod(profile.Id, container.Id, new PinnedPolicy(v1Folder));

        fx.Service.PrepareModRoot(profile.Id);

        var link = fx.StagedModLink(profile.Id, "DMF");
        Assert.True(Directory.Exists(link));
        Assert.True(IsSymlink(link));
        Assert.Equal(
            Path.Combine(fx.VersionDir(container.Id, v1Folder), "DMF"),
            ResolveLink(link));
    }

    [Fact]
    public void Moving_isLatest_requires_zero_profile_entry_changes()
    {
        // Acceptance #4: re-keying which version is "latest" is a repository-only
        // change. Two profiles, both Latest, share the same entry; the entry
        // never changes when the container's isLatest moves.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        var v1Folder = container.Versions.Single(v => v.VersionString == "1.0").Folder;
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        var link1 = fx.StagedModLink(profile.Id, "DMF");
        Assert.Equal(
            Path.Combine(fx.VersionDir(container.Id, v1Folder), "DMF"),
            ResolveLink(link1));

        // Add v2.0 (becomes isLatest). The profile entry is unchanged; staging
        // re-resolves dynamically.
        fx.AddVersion(container.Id, "2.0");
        var v2Folder = fx.Repo.Get(container.Id)!.Versions.Single(v => v.VersionString == "2.0").Folder;
        fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal(
            Path.Combine(fx.VersionDir(container.Id, v2Folder), "DMF"),
            ResolveLink(link1));
        var modEntry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.Equal(container.Id, modEntry.ContainerId); // unchanged
    }

    // ---- staging shape: symlinks only + mods.lst --------------------------

    [Fact]
    public void Staged_mods_dir_contains_only_symlinks_and_mods_lst_no_copied_files()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        // staged/ contains exactly the intermediate mods/ dir Relay now expects.
        var rootEntries = Directory.GetFileSystemEntries(fx.StagedDir(profile.Id));
        var rootEntry = Assert.Single(rootEntries);
        Assert.Equal("mods", Path.GetFileName(rootEntry));

        // staged/mods/ contains exactly the symlink + mods.lst.
        var modsDir = Path.Combine(fx.StagedDir(profile.Id), "mods");
        var stagedEntries = Directory.GetFileSystemEntries(modsDir);
        Assert.Equal(2, stagedEntries.Length);

        foreach (var entry in stagedEntries)
        {
            if (Path.GetFileName(entry) == "mods.lst")
            {
                var attrs = File.GetAttributes(entry);
                Assert.True((attrs & FileAttributes.ReparsePoint) == 0,
                    "mods.lst should be a real file, not a symlink");
            }
            else
            {
                Assert.True(IsSymlink(entry),
                    $"staged mod entry '{Path.GetFileName(entry)}' must be a symlink, not a copied directory");
            }
        }

        // The mod's actual files live only in the repository, under the base folder.
        Assert.True(File.Exists(Path.Combine(
            fx.VersionDir(container.Id, container.Versions[0].Folder), "DMF", "marker.txt")));
    }

    // ---- Windows junction (privilege-free staging link) --------------------

    [Fact]
    public void On_windows_staging_creates_a_junction_not_a_copy()
    {
        // Junctions are privilege-free on Windows (no Developer Mode / admin), so
        // staging works for a consumer release. The symlink tests above cover the
        // Linux path. Skipped on non-Windows so the suite stays green on Ubuntu CI.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        var link = fx.StagedModLink(profile.Id, "DMF");

        // The staged entry is a reparse point (a directory junction), not a
        // copied directory. A copy would NOT carry ReparsePoint.
        var attrs = File.GetAttributes(link);
        Assert.True((attrs & FileAttributes.ReparsePoint) != 0,
            "staged entry must be a junction (reparse point), not a copy");
        Assert.True((attrs & FileAttributes.Directory) != 0,
            "a directory junction carries the Directory attribute");

        // The junction points at the repository's base folder, proving the
        // target's files are not physically duplicated under staged/.
        Assert.Equal(
            Path.Combine(fx.VersionDir(container.Id, container.Versions[0].Folder), "DMF"),
            ResolveLink(link));

        // The target's files are visible through the junction (relay + DMF see a
        // normal mod folder), while the single source of truth stays in the repo.
        var markerThroughLink = Path.Combine(link, "marker.txt");
        Assert.True(File.Exists(markerThroughLink), "target's files are visible through the junction");
        Assert.Equal("DMF", File.ReadAllText(markerThroughLink));
        Assert.True(File.Exists(Path.Combine(
            fx.VersionDir(container.Id, container.Versions[0].Folder), "DMF", "marker.txt")),
            "the repository copy remains the source of truth");
    }

    // ---- regeneration ------------------------------------------------------

    [Fact]
    public void Regeneration_clears_and_rebuilds_staged_root()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var oldContainer = fx.AddContainerWithVersion("OldMod");
        fx.Service.AddMod(profile.Id, oldContainer.Id, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "OldMod")));

        // Remove OldMod from the profile; add NewMod.
        fx.Service.RemoveMod(profile.Id, oldContainer.Id);
        var newContainer = fx.AddContainerWithVersion("NewMod");
        fx.Service.AddMod(profile.Id, newContainer.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        // Old symlink gone; new one present; mods.lst reflects NewMod only.
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "OldMod")));
        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "NewMod")));
        Assert.Equal("NewMod\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Regeneration_never_follows_stale_symlinks_into_the_repository()
    {
        // Data-safety: a prior staged/mods/DMF symlink pointing into the repository
        // must be removed as a LINK (not followed), so the files survive a
        // regenerate. Guards ClearStagedDir's symlink-awareness.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        var versionPath = fx.VersionDir(container.Id, container.Versions[0].Folder);
        var marker = Path.Combine(versionPath, "DMF", "marker.txt");
        Assert.True(File.Exists(marker));

        // Regenerate (DMF now disabled -> not staged). The repository files survive.
        fx.Service.SetModEnabled(profile.Id, container.Id, enabled: false);
        fx.Service.PrepareModRoot(profile.Id);

        Assert.True(File.Exists(marker), "repository mod files must survive staged/ regeneration");
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
    }

    // ---- missing-version grace --------------------------------------------

    [Fact]
    public void Mod_whose_container_is_missing_is_skipped_not_crashed()
    {
        // A profile entry whose container is gone (a stale reference, or pruned)
        // is omitted from staged/ + mods.lst, with a warning. No exception.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var realContainer = fx.AddContainerWithVersion("RealMod");
        // A ghost: add an entry pointing at a non-existent container.
        fx.Service.AddMod(profile.Id, realContainer.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, Guid.NewGuid(), ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id); // must not throw

        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "Ghost")));
        // Only RealMod appears.
        Assert.Equal("RealMod\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Pinned_policy_with_unknown_version_is_skipped_with_warning()
    {
        // A pin to a version id that does not exist on the container: skip + warn.
        // The mod is omitted from staged/ + mods.lst. (The UI dropdown can't
        // produce such an id; this covers a programmatic / stale-id call.)
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        // AddMod does not validate the versionId (only SetModPolicy does); so a
        // phantom pin can be seeded here for the staging-skip assertion.
        fx.Service.AddMod(profile.Id, container.Id, new PinnedPolicy("no-such-version-id"));

        fx.Service.PrepareModRoot(profile.Id); // must not throw

        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
        Assert.Equal(string.Empty, File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Latest_policy_on_a_container_with_no_versions_is_skipped()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var empty = fx.Repo.CreateContainer(new UntrackedSource(), "Empty");
        fx.Service.AddMod(profile.Id, empty.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "Empty")));
        Assert.Equal(string.Empty, File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    // ---- base-name discovery (link name == on-disk base folder) -----------

    [Fact]
    public void Symlink_and_mods_lst_use_the_base_folder_name_not_the_display_name()
    {
        // The link name + mods.lst entry are the BASE folder name discovered
        // inside the version folder, not the container's display name. Mods bake
        // their folder name into their code, so the link must carry the base
        // name. Here the display name differs from the on-disk base on purpose.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "My Display Name");
        var withVersion = fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "actualbase"));
            File.WriteAllText(Path.Combine(dir, "actualbase", "actualbase.mod"), "x");
        });
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        // The symlink is named after the base folder; the display name is UI-only.
        var link = fx.StagedModLink(profile.Id, "actualbase");
        Assert.True(Directory.Exists(link));
        Assert.True(IsSymlink(link));
        Assert.Equal(
            Path.Combine(fx.VersionDir(container.Id, withVersion.Versions[0].Folder), "actualbase"),
            ResolveLink(link));
        Assert.Equal("actualbase\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    // NOTE: two mods with the same base folder name can't reach staging in normal
    // use: the add flow refuses them via IProfileService.GetBaseNameCollision
    // before importing (see ModListViewModelTests + ModListTests). Staging is a
    // simple loop with no dedupe, so a hand-edited profile.json that forces a
    // duplicate base name throws IOException on the second link (the accepted
    // "whatever it is" edge). No test asserts that edge: it is undefined behavior
    // the operator explicitly chose not to support.

    [Fact]
    public void Corrupted_version_folder_with_zero_subdirectories_is_skipped_with_warning()
    {
        // A version folder with no base subdir (e.g. legacy data predating the
        // import validation) cannot yield a base name; it is skipped + warned,
        // not crashed.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var real = fx.AddContainerWithVersion("RealMod");
        var bad = fx.Repo.CreateContainer(new UntrackedSource(), "BadMod");
        fx.Repo.AddVersion(bad.Id, "1.0", dir =>
        {
            // A loose file at the version root, no base subdir.
            File.WriteAllText(Path.Combine(dir, "loose.txt"), "x");
        });
        fx.Service.AddMod(profile.Id, real.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, bad.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id); // must not throw

        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "BadMod")));
        Assert.Equal("RealMod\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Corrupted_version_folder_with_multiple_subdirectories_is_skipped_with_warning()
    {
        // A version folder with more than one subdir is ambiguous (which is the
        // base?); it is skipped + warned rather than guessing.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var bad = fx.Repo.CreateContainer(new UntrackedSource(), "AmbiguousMod");
        fx.Repo.AddVersion(bad.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "a"));
            Directory.CreateDirectory(Path.Combine(dir, "b"));
        });
        fx.Service.AddMod(profile.Id, bad.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id); // must not throw

        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "AmbiguousMod")));
        Assert.Equal(string.Empty, File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    // ---- staging-link-failure path ----------------------------------------

    [Fact]
    public void Symlink_creation_failure_throws_io_exception_not_silent_copy()
    {
        StagingLinkCreator throwing = (_, _) =>
            throw new IOException("simulated: staging link could not be created");

        using var fx = new ProfileServiceFixture(createLink: throwing);
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var ex = Assert.Throws<IOException>(() => fx.Service.PrepareModRoot(profile.Id));
        Assert.Contains("staging link", ex.Message);

        // And it did NOT silently copy: no real mod dir under staged/.
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
    }

    // ---- SetModPolicy (no on-disk transition) ------------------------------

    [Fact]
    public void SetModPolicy_persists_and_round_trips_policy_with_no_on_disk_transition()
    {
        // The new model has no diverged copy to reconcile. SetModPolicy just
        // records the policy; staging re-resolves on the next PrepareModRoot.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P", string.Empty, new LaunchSettings());
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        fx.AddVersion(container.Id, "2.0"); // v2.0 is isLatest
        var v1Folder = container.Versions.Single(v => v.VersionString == "1.0").Folder;
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest); // -> v2.0

        // Pin to v1.0 by its version id; staging re-resolves to the v1.0 folder.
        fx.Service.SetModPolicy(profile.Id, container.Id, new PinnedPolicy(v1Folder));

        // Reload from disk (new service instance) to prove persistence.
        var reloadConfig = CuratorConfig.CreateDefault();
        reloadConfig.ProfilesBaseFolder = fx.BaseFolder;
        reloadConfig.ModsFolder = fx.ModsFolder;
        using var provider = new ServiceCollection()
            .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = reloadConfig })
            .AddLogging()
            .AddMods()
            .AddProfiles()
            .BuildServiceProvider();
        var reloaded = provider.GetRequiredService<IProfileService>();

        var entry = Assert.Single(reloaded.GetModList(profile.Id));
        Assert.Equal(v1Folder, Assert.IsType<PinnedPolicy>(entry.Policy).VersionId);

        // And staging reflects the pin.
        reloaded.PrepareModRoot(profile.Id);
        var link = fx.StagedModLink(profile.Id, "DMF");
        Assert.Equal(
            Path.Combine(fx.VersionDir(container.Id, v1Folder), "DMF"),
            ResolveLink(link));
    }

    // ---- helpers ------------------------------------------------------------

    private static bool IsSymlink(string path)
    {
        var attrs = File.GetAttributes(path);
        return (attrs & FileAttributes.ReparsePoint) != 0;
    }

    private static string ResolveLink(string path) =>
        File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
            ?? throw new IOException("not a link");
}
