using System.IO.Compression;
using System.Text;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

namespace Modificus.Curator.Mods.Tests;

/// <summary>
/// <see cref="IModImportService"/>: container find/create + version dedup +
/// <c>isLatest</c> flip + the <c>modName</c> path-traversal confinement retained
/// from Track B's review fix, AND the source-structure validation (both folder +
/// <c>.zip</c> require exactly one base directory with a matching
/// <c>&lt;base&gt;.mod</c> descriptor; the base folder is preserved under
/// <c>&lt;versionFolder&gt;/&lt;base&gt;/</c>), AND the two peeks the add flow
/// uses for the base-name collision hard-block:
/// <see cref="IModImportService.GetBaseName"/> (validates + returns the base name
/// without creating anything) + <see cref="IModImportService.FindExistingContainer"/>
/// (resolves the would-be dedup container, without creating anything). Uses a
/// temp <c>ModsFolder</c> + a DI-resolved service (black-box).
/// </summary>
public sealed class ModImportServiceTests
{
    // ---- container resolution (find/create) --------------------------------

    [Fact]
    public void Import_creates_a_new_untracked_container_when_absent()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

        var (containerId, versionId) = fx.Service.Import(sourceDir, "DMF", new UntrackedSource(), "1.0");

        var container = fx.Repo.Get(containerId);
        Assert.NotNull(container);
        Assert.Equal("DMF", container!.Name);
        Assert.IsType<UntrackedSource>(container.Source);
        // The returned id is the imported version's opaque folder id (a
        // ModVersion.Folder value), not the display tag, so the caller can pin
        // to exactly this version.
        Assert.Single(container.Versions);
        Assert.Equal(versionId, container.Versions[0].Folder);
    }

    [Fact]
    public void Import_dedups_untracked_by_name_on_re_import()
    {
        using var fx = new ImportFixture();
        var firstDir = fx.MakeSourceModFolder("SrcA", ("a.txt", "a"));
        var (firstId, _) = fx.Service.Import(firstDir, "DMF", new UntrackedSource(), "1.0");

        var secondDir = fx.MakeSourceModFolder("SrcB", ("b.txt", "b"));
        var (secondId, _) = fx.Service.Import(secondDir, "DMF", new UntrackedSource(), "1.0");

        // Same container (dedup by name).
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public void Import_dedups_Nexus_by_mod_id_on_re_import()
    {
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };

        var (firstId, _) = fx.Service.Import(dir, "WT-A", nexus, "1.0");
        var (secondId, _) = fx.Service.Import(dir, "WT-B", nexus, "2.0"); // different name, same source

        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public void Import_does_not_dedup_across_different_sources()
    {
        // Goal #4: different sources never collide. Same name, different source
        // types yield different containers.
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");

        var (untrackedId, _) = fx.Service.Import(dir, "WT", new UntrackedSource(), "1.0");
        var (nexusId, _) = fx.Service.Import(dir, "WT", new NexusSource { ModId = 99 }, "1.0");

        Assert.NotEqual(untrackedId, nexusId);
    }

    // ---- version resolution (dedup + isLatest flip) -----------------------

    [Fact]
    public void Import_same_versionString_dedups_reusing_the_version_folder()
    {
        using var fx = new ImportFixture();
        var firstDir = fx.MakeSourceModFolder("SrcA", ("a.txt", "a"));
        var (containerId, _) = fx.Service.Import(firstDir, "DMF", new UntrackedSource(), "1.0");
        var firstFolder = fx.Repo.Get(containerId)!.Versions[0].Folder;

        var secondDir = fx.MakeSourceModFolder("SrcB", ("b.txt", "b"));
        var (_, _) = fx.Service.Import(secondDir, "DMF", new UntrackedSource(), "1.0");

        var container = fx.Repo.Get(containerId);
        var version = Assert.Single(container!.Versions);
        Assert.Equal(firstFolder, version.Folder); // folder reused
        Assert.Equal("1.0", version.VersionString);

        // Files refreshed (no merge). The reused version folder now holds the
        // second import's base folder (SrcB), with SrcA gone.
        var versionPath = fx.Repo.GetVersionFolderPath(containerId, firstFolder);
        Assert.False(Directory.Exists(Path.Combine(versionPath, "SrcA")));
        Assert.True(File.Exists(Path.Combine(versionPath, "SrcB", "b.txt")));
        Assert.True(File.Exists(Path.Combine(versionPath, "SrcB", "SrcB.mod")));
    }

    [Fact]
    public void Import_new_versionString_flips_isLatest_to_the_new_version()
    {
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var (containerId, _) = fx.Service.Import(dir, "DMF", new UntrackedSource(), "1.0");
        var (_, _) = fx.Service.Import(dir, "DMF", new UntrackedSource(), "2.0");

        var container = fx.Repo.Get(containerId);
        Assert.Equal(2, container!.Versions.Count);
        var latest = Assert.Single(container.Versions, v => v.IsLatest);
        Assert.Equal("2.0", latest.VersionString);
    }

    // ---- remote publish-date propagation (Nexus update-check basis) --------

    [Fact]
    public void Import_records_RemoteUploadedAt_on_a_new_Nexus_version()
    {
        // The Nexus update-check compares the imported file's publish date
        // (RemoteUploadedAt), not Curator's import time, against the latest file.
        // The acquisition layer captures the publish date and forwards it
        // through Import; Import forwards it to AddVersion; AddVersion stamps
        // it on the new ModVersion entry. This test pins the new-version path.
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };
        var publishedAt = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);

        var (containerId, _) = fx.Service.Import(dir, "WT", nexus, "1.0", publishedAt);

        var version = Assert.Single(fx.Repo.Get(containerId)!.Versions);
        Assert.Equal(publishedAt, version.RemoteUploadedAt);
    }

    [Fact]
    public void Import_default_remoteUploadedAt_is_null_for_manual_imports()
    {
        // Manual imports (folder/archive via the picker, drag-and-drop) call
        // Import without the param -> null. Non-Nexus aren't update-checked
        // anyway; the null is the correct value (and the check's fallback to
        // ImportedAt preserves the prior behavior).
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");

        var (containerId, _) = fx.Service.Import(dir, "DMF", new UntrackedSource(), "1.0");

        var version = Assert.Single(fx.Repo.Get(containerId)!.Versions);
        Assert.Null(version.RemoteUploadedAt);
    }

    [Fact]
    public void Import_dedup_refreshes_RemoteUploadedAt_on_re_import()
    {
        // Re-importing the same VersionString refreshes the files AND
        // RemoteUploadedAt (mirroring how dedup refreshes files): a
        // re-acquired version carries the current remote-publish timestamp,
        // not the stale one from the first import. This is what makes a
        // one-click update land the new file's publish date on the
        // reused entry, so the next check does not re-flag it.
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };
        var firstPublished = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var refreshedPublished = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Service.Import(dir, "WT", nexus, "1.0", firstPublished);
        var (containerId, _) = fx.Service.Import(dir, "WT", nexus, "1.0", refreshedPublished);

        var version = Assert.Single(fx.Repo.Get(containerId)!.Versions);
        Assert.Equal(refreshedPublished, version.RemoteUploadedAt);
    }

    [Fact]
    public void Import_persists_RemoteUploadedAt_through_a_new_repository_instance()
    {
        // The field round-trips through container.json (no migration; STJ
        // default for a missing nullable is null). A fresh repo reading the
        // manifest from disk must observe the captured publish date.
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };
        var publishedAt = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);

        var (containerId, _) = fx.Service.Import(dir, "WT", nexus, "1.0", publishedAt);

        var reloaded = fx.ReloadRepo();
        var version = Assert.Single(reloaded.Get(containerId)!.Versions);
        Assert.Equal(publishedAt, version.RemoteUploadedAt);
    }

    [Fact]
    public void Import_with_null_RemoteUploadedAt_clears_a_previously_recorded_one_on_dedup()
    {
        // Edge case: a Nexus version is imported with a publish date, then the
        // SAME VersionString is re-imported manually (no param). Dedup
        // overwrites RemoteUploadedAt to null (matching how it refreshes
        // files). Non-Nexus aren't update-checked anyway, so the null is
        // benign; pinning the behavior keeps it consistent with the
        // "dedup refreshes everything from the new call" semantic.
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };
        var publishedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Service.Import(dir, "WT", nexus, "1.0", publishedAt);
        var (containerId, _) = fx.Service.Import(dir, "WT", nexus, "1.0"); // no param -> null

        var version = Assert.Single(fx.Repo.Get(containerId)!.Versions);
        Assert.Null(version.RemoteUploadedAt);
    }

    // ---- display metadata pass-through (Nexus acquisition basis) ----------

    [Fact]
    public void Import_forwards_displayMetadata_to_the_container_on_a_new_version()
    {
        // The acquisition layer captures display metadata and forwards it
        // through Import; Import forwards it to AddVersion; AddVersion stamps
        // it on the container in the same manifest update as the new entry.
        // Source-agnostic: the seam accepts the value object, no Nexus type
        // crosses the boundary into Mods.
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };
        var metadata = new ModDisplayMetadata
        {
            Summary = "From Nexus.",
            ThumbnailUrl = "https://example.com/thumb.png",
            IsAdultContent = true,
        };

        var (containerId, _) = fx.Service.Import(dir, "WT", nexus, "1.0", null, metadata);

        var container = fx.Repo.Get(containerId);
        Assert.Equal(metadata, container!.DisplayMetadata);
    }

    [Fact]
    public void Import_forwards_displayMetadata_on_a_dedup_re_import()
    {
        // Re-importing the same versionString with metadata replaces the prior
        // metadata in the same manifest update as the dedup refresh (matching
        // how dedup refreshes RemoteUploadedAt).
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };
        var first = new ModDisplayMetadata { Summary = "first" };
        var second = new ModDisplayMetadata { Summary = "second" };

        fx.Service.Import(dir, "WT", nexus, "1.0", null, first);
        var (containerId, _) = fx.Service.Import(dir, "WT", nexus, "1.0", null, second);

        var container = fx.Repo.Get(containerId);
        Assert.Equal(second, container!.DisplayMetadata);
    }

    [Fact]
    public void Import_with_null_displayMetadata_preserves_existing_metadata()
    {
        // The load-bearing guarantee: a manual re-import (folder/archive via the
        // picker, no metadata argument) never erases a prior Nexus acquisition
        // or backfill. The default-argument path leaves the prior value intact.
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };
        var metadata = new ModDisplayMetadata { Summary = "captured once" };
        fx.Service.Import(dir, "WT", nexus, "1.0", null, metadata);

        var (containerId, _) = fx.Service.Import(dir, "WT", nexus, "1.0"); // no metadata arg

        var container = fx.Repo.Get(containerId);
        Assert.Equal(metadata, container!.DisplayMetadata);
    }

    [Fact]
    public void Import_persists_displayMetadata_through_a_new_repository_instance()
    {
        // The nested object round-trips through container.json (STJ default
        // options, no migration). A fresh repo reading the manifest from disk
        // observes the captured metadata.
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };
        var metadata = new ModDisplayMetadata
        {
            Summary = "persisted",
            ThumbnailUrl = "https://example.com/thumb.png",
        };

        var (containerId, _) = fx.Service.Import(dir, "WT", nexus, "1.0", null, metadata);

        var reloaded = fx.ReloadRepo();
        Assert.Equal(metadata, reloaded.Get(containerId)!.DisplayMetadata);
    }

    [Fact]
    public void Import_validation_failure_leaves_no_container_and_no_metadata()
    {
        // An invalid source (bad folder shape) throws before any container or
        // version is created, so there is no metadata to set either. Source-
        // structure validation precedes the metadata-aware AddVersion call.
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceFolder("NoDescriptor", ("scripts/init.lua", "x"));
        var metadata = new ModDisplayMetadata { Summary = "ignored" };

        Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(sourceDir, "NoDescriptor", new UntrackedSource(), "1.0", null, metadata));
        Assert.Empty(fx.Repo.List());
    }

    // ---- folder + zip placement (base folder preserved) -------------------

    [Fact]
    public void Import_folder_copies_the_folder_itself_into_the_version_folder()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder(
            "Src", ("readme.txt", "hi"), ("sub/nested.txt", "nested"));

        var (containerId, _) = fx.Service.Import(sourceDir, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        // The folder is copied ITSELF (name preserved), not its contents, so the
        // shape is <versionPath>/Src/<files> (consistent with the zip path).
        Assert.Equal("hi", File.ReadAllText(Path.Combine(versionPath, "Src", "readme.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(versionPath, "Src", "sub", "nested.txt")));
        Assert.True(File.Exists(Path.Combine(versionPath, "Src", "Src.mod")));
    }

    [Fact]
    public void Import_zip_extracts_preserving_the_single_top_level_base_folder()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mod.zip");
        fx.MakeModZip(zipPath, "dmf", ("readme.txt", "hi"), ("sub/nested.txt", "nested"));

        var (containerId, _) = fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        // The single top-level base folder is preserved at the version root:
        // <versionPath>/dmf/<files>.
        Assert.Equal("hi", File.ReadAllText(Path.Combine(versionPath, "dmf", "readme.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(versionPath, "dmf", "sub", "nested.txt")));
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "dmf.mod")));
    }

    [Theory]
    [InlineData("MOD.ZIP")]
    [InlineData("mod.Zip")]
    public void Import_imports_archive_regardless_of_extension_case(string zipName)
    {
        // Detection is content-based (magic bytes), so the extension casing is
        // irrelevant. Kept from the zip-only era to confirm no regression.
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, zipName);
        fx.MakeModZip(zipPath, "dmf", ("file.txt", "x"));

        var (containerId, _) = fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "file.txt")));
    }

    [Fact]
    public void Import_7z_extracts_preserving_the_single_top_level_base_folder()
    {
        using var fx = new ImportFixture();
        var sevenZPath = Path.Combine(fx.TempRoot, "mod.7z");
        fx.MakeModSevenZip(sevenZPath, "dmf", ("readme.txt", "hi"), ("sub/nested.txt", "nested"));

        var (containerId, _) = fx.Service.Import(sevenZPath, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(versionPath, "dmf", "readme.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(versionPath, "dmf", "sub", "nested.txt")));
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "dmf.mod")));
    }

    [Fact]
    public void Import_rar_extracts_preserving_the_single_top_level_base_folder()
    {
        // Uses the committed real-RAR5 fixture (SharpCompress can read rar but
        // not write it; the operator builds the .rar from Fixtures/rarfixture/).
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "rarfixture.rar");
        Assert.True(File.Exists(fixturePath),
            $"RAR fixture not found at {fixturePath}. Ensure Fixtures/rarfixture.rar is copied to the output directory.");

        using var fx = new ImportFixture();
        var (containerId, _) = fx.Service.Import(fixturePath, "RarMod", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        // The fixture ships rarfixture/rarfixture.mod + rarfixture/scripts/hello.lua.
        Assert.True(File.Exists(Path.Combine(versionPath, "rarfixture", "rarfixture.mod")));
        Assert.True(File.Exists(Path.Combine(versionPath, "rarfixture", "scripts", "hello.lua")));
    }

    [Fact]
    public void Import_detects_archive_by_content_not_extension()
    {
        // A .7z archive renamed to .zip: content detection ignores the extension
        // and reads the 7z magic bytes. This is the inversion of the old
        // rename-to-.zip workaround: the code now accommodates the real input.
        using var fx = new ImportFixture();
        var sevenZBytes = Path.Combine(fx.TempRoot, "real.7z");
        fx.MakeModSevenZip(sevenZBytes, "dmf", ("file.txt", "x"));
        var mislabeled = Path.Combine(fx.TempRoot, "mislabeled.zip");
        File.Move(sevenZBytes, mislabeled);

        var (containerId, _) = fx.Service.Import(mislabeled, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "file.txt")));
    }

    // ---- source-structure validation (zip + folder) -----------------------
    //
    // Both import kinds require exactly one base directory with a <base>.mod
    // descriptor (filename matches the base folder name). Validation runs BEFORE
    // any file is placed or container/version is created, so an invalid source
    // leaves nothing on disk + creates no container.

    [Fact]
    public void Import_zip_with_single_base_and_matching_descriptor_succeeds_and_preserves_structure()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mod.zip");
        fx.MakeModZip(zipPath, "dmf", ("scripts/init.lua", "-- dmf"));

        var (containerId, _) = fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(Directory.Exists(Path.Combine(versionPath, "dmf")));
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "dmf.mod")));
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "scripts", "init.lua")));
    }

    [Fact]
    public void Import_zip_with_multiple_top_level_folders_is_rejected_and_extracts_nothing()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "two-mods.zip");
        MakeZip(zipPath, ("modA/modA.mod", "a"), ("modB/modB.mod", "b"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(zipPath, "TwoMods", new UntrackedSource(), "1.0"));
        Assert.Contains("exactly one top-level mod folder", ex.Message);

        // No container created, nothing extracted (validation precedes both).
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_zip_with_loose_top_level_files_is_rejected()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "loose.zip");
        MakeZip(zipPath, ("readme.txt", "no folder here"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(zipPath, "Loose", new UntrackedSource(), "1.0"));
        Assert.Contains("loose top-level file", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_zip_missing_the_base_mod_descriptor_is_rejected()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "no-mod.zip");
        // Base folder present, but no dmf/dmf.mod.
        MakeZip(zipPath, ("dmf/scripts/init.lua", "-- no descriptor"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0"));
        Assert.Contains("dmf/dmf.mod", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_zip_with_a_mismatched_descriptor_name_is_rejected()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mismatch.zip");
        // Base folder 'dmf', but the descriptor is 'other.mod' (not 'dmf.mod').
        MakeZip(zipPath, ("dmf/other.mod", "wrong name"));

        Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0"));
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_7z_with_multiple_top_level_folders_is_rejected()
    {
        // Structure validation is format-agnostic: the same single-base +
        // descriptor invariant enforced for zip applies to 7z too.
        using var fx = new ImportFixture();
        var sevenZPath = Path.Combine(fx.TempRoot, "two-mods.7z");
        MakeSevenZip(sevenZPath, ("modA/modA.mod", "a"), ("modB/modB.mod", "b"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(sevenZPath, "TwoMods", new UntrackedSource(), "1.0"));
        Assert.Contains("exactly one top-level mod folder", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_archive_with_explicit_directory_entries_extracts_correctly()
    {
        // An archive that ships explicit directory entries (not just file entries
        // whose paths imply directories). Directory entries are skipped
        // (IsDirectory check); directories are created implicitly by the
        // file-entry writer. The CVE-vulnerable directory-entry branch is never
        // reached.
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "with-dirs.zip");
        MakeZip(zipPath,
            ("mymod/", ""),                          // explicit directory entry
            ("mymod/scripts/", ""),                  // explicit directory entry
            ("mymod/mymod.mod", "mymod"),
            ("mymod/scripts/hello.lua", "-- hello"));

        var (containerId, _) = fx.Service.Import(zipPath, "WithDirs", new UntrackedSource(), "1.0");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(File.Exists(Path.Combine(versionPath, "mymod", "mymod.mod")));
        Assert.True(File.Exists(Path.Combine(versionPath, "mymod", "scripts", "hello.lua")));
    }

    [Fact]
    public void Import_rejects_archive_entries_that_escape_the_extraction_directory()
    {
        // Path-traversal guard: a crafted zip carries a valid base folder +
        // descriptor (so it passes structure validation) alongside a nested
        // traversal entry (../..) that tries to escape above the version dir.
        // AssertSafePath catches the escape before any file is written, and
        // nothing lands outside the version dir.
        //
        // The malicious zip is built with System.IO.Compression.ZipArchive in
        // Create mode (the BCL allows arbitrary entry keys like "../"; test
        // helper MakeZip uses it).
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "malicious.zip");
        const string marker = "traversal_escape_marker.txt";
        MakeZip(zipPath,
            ("mymod/mymod.mod", "valid"),                          // passes validation
            ($"mymod/../../../../{marker}", "escaped payload"));   // nested traversal

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(zipPath, "Malicious", new UntrackedSource(), "1.0"));
        Assert.Contains("escapes the extraction directory", ex.Message);

        // The marker must not exist anywhere under the test temp root: the
        // guard threw before WriteToDirectory, so no file escaped.
        var written = Directory.GetFiles(fx.TempRoot, marker, SearchOption.AllDirectories);
        Assert.Empty(written);
    }

    [Fact]
    public void Import_folder_preserves_the_folder_name_and_validates_mod_descriptor()
    {
        using var fx = new ImportFixture();
        // Container display name differs from the base folder name; the base
        // folder name (not the display name) is what's preserved on disk.
        var sourceDir = fx.MakeSourceModFolder("Power_DI", ("scripts/init.lua", "-- pi"));

        var (containerId, _) = fx.Service.Import(sourceDir, "Power DI", new UntrackedSource(), "1.2");

        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(Directory.Exists(Path.Combine(versionPath, "Power_DI")));
        Assert.True(File.Exists(Path.Combine(versionPath, "Power_DI", "Power_DI.mod")));
        Assert.True(File.Exists(Path.Combine(versionPath, "Power_DI", "scripts", "init.lua")));
    }

    [Fact]
    public void Import_folder_missing_the_base_mod_descriptor_is_rejected()
    {
        using var fx = new ImportFixture();
        // A folder with files but no matching <folder>.mod.
        var sourceDir = fx.MakeSourceFolder("NoDescriptor", ("scripts/init.lua", "x"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(sourceDir, "NoDescriptor", new UntrackedSource(), "1.0"));
        Assert.Contains("NoDescriptor.mod", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_folder_that_is_a_parent_of_mods_is_rejected()
    {
        // The picked folder must BE the mod, not a parent containing several
        // mods. A parent has no matching <parent>.mod, so it's rejected (this is
        // the structural guard against a mis-pick).
        using var fx = new ImportFixture();
        var parentDir = Path.Combine(fx.TempRoot, "MyMods");
        Directory.CreateDirectory(Path.Combine(parentDir, "modA"));
        Directory.CreateDirectory(Path.Combine(parentDir, "modB"));
        File.WriteAllText(Path.Combine(parentDir, "modA", "modA.mod"), "a");
        File.WriteAllText(Path.Combine(parentDir, "modB", "modB.mod"), "b");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(parentDir, "MyMods", new UntrackedSource(), "1.0"));
        Assert.Contains("MyMods.mod", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_folder_that_is_empty_is_rejected()
    {
        using var fx = new ImportFixture();
        var sourceDir = Path.Combine(fx.TempRoot, "Empty");
        Directory.CreateDirectory(sourceDir);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(sourceDir, "Empty", new UntrackedSource(), "1.0"));
        Assert.Contains("empty", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    // ---- empty version + round-trip ---------------------------------------

    [Fact]
    public void Import_with_empty_version_records_an_empty_version_string()
    {
        // Untracked imports pass string.Empty for the version (no tag).
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

        var (containerId, versionId) = fx.Service.Import(sourceDir, "Local", new UntrackedSource(), "");

        var version = Assert.Single(fx.Repo.Get(containerId)!.Versions);
        Assert.Equal(string.Empty, version.VersionString);
        // The returned id is the version's opaque folder id (independent of the
        // display tag, which is empty here for an untracked import).
        Assert.Equal(versionId, version.Folder);
    }

    [Fact]
    public void Import_persists_container_and_version_through_a_new_repository_instance()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

        var (containerId, _) = fx.Service.Import(
            sourceDir,
            "WeaponTweaks",
            new NexusSource { ModId = 4242 },
            "v1.2.3");

        var reloaded = fx.ReloadRepo();
        var container = reloaded.Get(containerId);
        Assert.NotNull(container);
        Assert.Equal("WeaponTweaks", container!.Name);
        var nexus = Assert.IsType<NexusSource>(container.Source);
        Assert.Equal(4242, nexus.ModId);
        var version = Assert.Single(container.Versions);
        Assert.Equal("v1.2.3", version.VersionString);
    }

    // ---- error paths + modName confinement (retained from Track B) --------

    [Fact]
    public void Import_throws_FileNotFoundException_when_source_does_not_exist()
    {
        using var fx = new ImportFixture();
        var ghost = Path.Combine(fx.TempRoot, "nope");

        Assert.Throws<FileNotFoundException>(() =>
            fx.Service.Import(ghost, "DMF", new UntrackedSource(), "1.0"));
    }

    [Fact]
    public void Import_throws_unsupported_format_for_non_archive_file()
    {
        // A plain text file is not an archive: content detection
        // (ArchiveFactory.IsArchive, magic bytes) returns false, and the service
        // throws InvalidOperationException with the actionable message before
        // any decompression library is invoked. The old zip-only path threw
        // InvalidDataException from inside ZipFile.Open; content-based detection
        // produces a clearer, earlier error.
        using var fx = new ImportFixture();
        var notArchive = Path.Combine(fx.TempRoot, "broken.zip");
        File.WriteAllBytes(notArchive, Encoding.UTF8.GetBytes("this is not a zip archive"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Service.Import(notArchive, "DMF", new UntrackedSource(), "1.0"));
        Assert.Contains("couldn't read", ex.Message);
        Assert.Contains("extract the file yourself", ex.Message);
        // No container created for an unsupported file.
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_throws_InvalidDataException_for_a_truncated_archive()
    {
        // A truncated zip (valid magic bytes, incomplete body): content detection
        // passes (it is a real archive start), but SharpCompress cannot open it.
        // The service catches the SharpCompress failure and rethrows it as
        // InvalidDataException with the "try downloading again" message.
        using var fx = new ImportFixture();
        var validZip = Path.Combine(fx.TempRoot, "valid.zip");
        fx.MakeModZip(validZip, "dmf", ("scripts/init.lua", "-- dmf"));
        var bytes = File.ReadAllBytes(validZip);
        // Keep the zip magic bytes (PK\x03\x04 = 4 bytes) but truncate the rest.
        Array.Resize(ref bytes, Math.Max(4, bytes.Length / 4));
        var truncatedZip = Path.Combine(fx.TempRoot, "truncated.zip");
        File.WriteAllBytes(truncatedZip, bytes);

        var ex = Assert.ThrowsAny<InvalidDataException>(() =>
            fx.Service.Import(truncatedZip, "DMF", new UntrackedSource(), "1.0"));
        Assert.Contains("corrupted or incomplete", ex.Message);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Import_throws_ArgumentException_for_null_or_whitespace_modName()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, "", new UntrackedSource(), "1.0"));
        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, "   ", new UntrackedSource(), "1.0"));
    }

    // modName confinement: the import target is now an opaque UUID folder
    // (not modName-derived), but the confinement is retained as defense-in-depth
    // (a malformed name would later clash with the untracked-by-name index).

    [Theory]
    [InlineData("../evil")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Import_rejects_modName_that_escapes_or_nests_under_the_shared_root(string modName)
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, modName, new UntrackedSource(), "1.0"));

        // Defense: a rejected name must not have created or deleted anything
        // outside the mod root (the "../evil" case in particular).
        Assert.False(Directory.Exists(Path.Combine(fx.TempRoot, "evil")));
    }

    [Fact]
    public void Import_rejects_an_absolute_modName()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");
        var absolute = Path.Combine(Path.GetPathRoot(Directory.GetCurrentDirectory())!, "abs-mod");

        Assert.Throws<ArgumentException>(() =>
            fx.Service.Import(sourceDir, absolute, new UntrackedSource(), "1.0"));
    }

    [Fact]
    public void Import_accepts_a_normal_single_segment_modName()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

        var (containerId, _) = fx.Service.Import(sourceDir, "WeaponTweaks", new UntrackedSource(), "1.2");

        var container = fx.Repo.Get(containerId);
        Assert.Equal("WeaponTweaks", container!.Name);
    }

    [Fact]
    public void Import_rejects_null_arguments()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Src");

        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.Import(null!, "DMF", new UntrackedSource(), "1.0"));
        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.Import(sourceDir, "DMF", null!, "1.0"));
        Assert.Throws<ArgumentNullException>(() =>
            fx.Service.Import(sourceDir, "DMF", new UntrackedSource(), null!));
    }

    [Fact]
    public void Import_creates_ModsFolder_on_first_use()
    {
        // First-import safety: a missing ModsFolder is created.
        using var fx = new ImportFixture();
        if (Directory.Exists(fx.ModsFolder))
        {
            Directory.Delete(fx.ModsFolder, recursive: true);
        }
        var sourceDir = fx.MakeSourceModFolder("Src");

        fx.Service.Import(sourceDir, "DMF", new UntrackedSource(), "1.0");

        Assert.True(Directory.Exists(fx.ModsFolder));
    }

    // ---- GetBaseName (peek) ------------------------------------------------
    //
    // GetBaseName validates the source structure + returns the base folder name
    // WITHOUT creating a container or version. Same validation as Import (reuses
    // the private validators); an invalid source throws, leaving nothing on disk.

    [Fact]
    public void GetBaseName_returns_the_base_name_from_a_valid_zip_without_creating_anything()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mod.zip");
        fx.MakeModZip(zipPath, "dmf", ("scripts/init.lua", "-- dmf"));

        var baseName = fx.Service.GetBaseName(zipPath);

        Assert.Equal("dmf", baseName);
        // A peek creates nothing: no container, no version folder.
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void GetBaseName_returns_the_base_name_from_a_valid_folder_without_creating_anything()
    {
        using var fx = new ImportFixture();
        var sourceDir = fx.MakeSourceModFolder("Power_DI", ("scripts/init.lua", "-- pi"));

        var baseName = fx.Service.GetBaseName(sourceDir);

        Assert.Equal("Power_DI", baseName);
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void GetBaseName_throws_for_an_invalid_source_and_creates_nothing()
    {
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "two-mods.zip");
        MakeZip(zipPath, ("modA/modA.mod", "a"), ("modB/modB.mod", "b"));

        var ex = Assert.Throws<InvalidOperationException>(() => fx.Service.GetBaseName(zipPath));
        Assert.Contains("exactly one top-level mod folder", ex.Message);

        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void GetBaseName_throws_for_a_missing_source()
    {
        using var fx = new ImportFixture();
        Assert.Throws<FileNotFoundException>(() =>
            fx.Service.GetBaseName(Path.Combine(fx.TempRoot, "nope")));
    }

    [Fact]
    public void GetBaseName_then_Import_produce_the_same_base_folder()
    {
        // The peek + the import agree on the base folder name (both run the same
        // validation); Import then places files under <versionFolder>/<base>/.
        using var fx = new ImportFixture();
        var zipPath = Path.Combine(fx.TempRoot, "mod.zip");
        fx.MakeModZip(zipPath, "dmf", ("scripts/init.lua", "-- dmf"));

        var peeked = fx.Service.GetBaseName(zipPath);
        var (containerId, _) = fx.Service.Import(zipPath, "DMF", new UntrackedSource(), "1.0");

        Assert.Equal("dmf", peeked);
        var versionPath = fx.Repo.GetVersionFolderPath(containerId,
            fx.Repo.Get(containerId)!.Versions[0].Folder);
        Assert.True(Directory.Exists(Path.Combine(versionPath, "dmf")));
        Assert.True(File.Exists(Path.Combine(versionPath, "dmf", "dmf.mod")));
    }

    // ---- FindExistingContainer (dedup peek) --------------------------------
    //
    // FindExistingContainer resolves the container an import WOULD dedup to,
    // without creating anything. Mirrors Import's container resolution minus the
    // create (the dedup rules live in one place: this service).

    [Fact]
    public void FindExistingContainer_returns_the_untracked_container_by_name()
    {
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var (createdId, _) = fx.Service.Import(dir, "DMF", new UntrackedSource(), "1.0");

        var found = fx.Service.FindExistingContainer(new UntrackedSource(), "DMF");

        Assert.NotNull(found);
        Assert.Equal(createdId, found!.Id);
    }

    [Fact]
    public void FindExistingContainer_returns_null_for_an_unknown_untracked_name()
    {
        using var fx = new ImportFixture();

        var found = fx.Service.FindExistingContainer(new UntrackedSource(), "Nobody");

        Assert.Null(found);
    }

    [Fact]
    public void FindExistingContainer_creates_nothing()
    {
        using var fx = new ImportFixture();

        _ = fx.Service.FindExistingContainer(new UntrackedSource(), "Ghost");

        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void FindExistingContainer_resolves_nexus_by_mod_id()
    {
        using var fx = new ImportFixture();
        var dir = fx.MakeSourceModFolder("Src");
        var nexus = new NexusSource { ModId = 4242 };
        var (createdId, _) = fx.Service.Import(dir, "WT", nexus, "1.0");

        var found = fx.Service.FindExistingContainer(new NexusSource { ModId = 4242 }, "other");

        Assert.NotNull(found);
        Assert.Equal(createdId, found!.Id);
    }

    [Fact]
    public void FindExistingContainer_resolves_linked_by_external_path()
    {
        using var fx = new ImportFixture();
        var external = fx.MakeExternalModFolder("External", ("file.txt", "x"));
        var createdId = fx.Service.LinkFolder(external);

        // modName is ignored for linked; identity is the external path.
        var found = fx.Service.FindExistingContainer(
            new LinkedSource { ExternalPath = Path.GetFullPath(external) },
            "ignored");

        Assert.NotNull(found);
        Assert.Equal(createdId, found!.Id);
    }

    [Fact]
    public void FindExistingContainer_returns_null_for_an_unlinked_external_path()
    {
        using var fx = new ImportFixture();
        var external = fx.MakeExternalModFolder("Standalone", ("file.txt", "x"));

        Assert.Null(fx.Service.FindExistingContainer(
            new LinkedSource { ExternalPath = Path.GetFullPath(external) },
            "ignored"));
    }

    // ---- LinkFolder (linked-folder add) ------------------------------------
    //
    // LinkFolder records an external folder as a repository container without
    // copying. It reuses the folder-shape validator, rejects any target that
    // overlaps a Curator-managed root, and returns the existing container id on
    // a re-link of the same path.

    [Fact]
    public void LinkFolder_creates_a_linked_container_without_copying_the_target()
    {
        using var fx = new ImportFixture();
        var external = fx.MakeExternalModFolder("ExternalMod", ("script.lua", "return 1"));
        var sentinelPath = Path.Combine(external, "ExternalMod.mod");
        var sentinelContent = File.ReadAllText(sentinelPath);

        var id = fx.Service.LinkFolder(external);

        var container = fx.Repo.Get(id);
        Assert.NotNull(container);
        Assert.IsType<LinkedSource>(container!.Source);
        Assert.Equal("ExternalMod", container.Name);
        Assert.Empty(container.Versions); // no versions, no version folders

        // No copy under the mods root: only the new container's manifest dir.
        var containerDir = Path.Combine(fx.ModsFolder, id.ToString());
        Assert.True(File.Exists(Path.Combine(containerDir, "container.json")));
        var entries = Directory.GetFileSystemEntries(containerDir);
        Assert.Single(entries); // container.json only, no version subfolder

        // The external target is untouched.
        Assert.True(File.Exists(sentinelPath));
        Assert.Equal(sentinelContent, File.ReadAllText(sentinelPath));
    }

    [Fact]
    public void LinkFolder_re_linking_the_same_path_returns_the_same_container_id_and_touches_nothing()
    {
        using var fx = new ImportFixture();
        var external = fx.MakeExternalModFolder("ExternalMod");
        var firstId = fx.Service.LinkFolder(external);

        // Mutate the external target between links; a refresh must NOT copy or
        // delete anything, so the mutation survives.
        File.WriteAllText(Path.Combine(external, "added.txt"), "fresh");

        var secondId = fx.Service.LinkFolder(external);

        Assert.Equal(firstId, secondId);
        // Still exactly one linked container.
        var single = Assert.Single(fx.Repo.List());
        Assert.Equal(firstId, single.Id);
        // The mutation survives (no refresh-time delete/copy).
        Assert.True(File.Exists(Path.Combine(external, "added.txt")));
    }

    [Fact]
    public void LinkFolder_rejects_a_non_absolute_path()
    {
        using var fx = new ImportFixture();
        Assert.Throws<ArgumentException>(() => fx.Service.LinkFolder("relative/path/ExternalMod"));
        Assert.Empty(fx.Repo.List()); // nothing created
    }

    [Fact]
    public void LinkFolder_rejects_a_missing_folder()
    {
        using var fx = new ImportFixture();
        var missing = Path.Combine(fx.TempRoot, "DoesNotExist");

        Assert.Throws<InvalidOperationException>(() => fx.Service.LinkFolder(missing));
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void LinkFolder_rejects_a_file_rather_than_a_directory()
    {
        using var fx = new ImportFixture();
        var file = Path.Combine(fx.TempRoot, "notafolder.txt");
        File.WriteAllText(file, "x");

        Assert.Throws<InvalidOperationException>(() => fx.Service.LinkFolder(file));
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void LinkFolder_rejects_a_bad_folder_shape_with_no_matching_descriptor()
    {
        using var fx = new ImportFixture();
        // A folder with files but no <base>.mod descriptor.
        var bad = Path.Combine(fx.TempRoot, "BadMod");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "script.lua"), "x");

        Assert.Throws<InvalidOperationException>(() => fx.Service.LinkFolder(bad));
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void LinkFolder_rejects_a_target_inside_the_mods_root()
    {
        using var fx = new ImportFixture();
        var inside = Path.Combine(fx.ModsFolder, "Inside");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "Inside.mod"), "x");

        Assert.Throws<InvalidOperationException>(() => fx.Service.LinkFolder(inside));
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void LinkFolder_rejects_a_target_inside_the_profiles_root()
    {
        using var fx = new ImportFixture();
        var profilesBase = fx.ProfilesBaseFolder;
        var inside = Path.Combine(profilesBase, "Inside");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "Inside.mod"), "x");

        Assert.Throws<InvalidOperationException>(() => fx.Service.LinkFolder(inside));
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void LinkFolder_rejects_a_target_that_contains_the_mods_root()
    {
        // The mods root lives under TempRoot; linking TempRoot itself would
        // make Curator's operations recurse into the target.
        using var fx = new ImportFixture();
        Assert.Throws<InvalidOperationException>(() => fx.Service.LinkFolder(fx.TempRoot));
        Assert.Empty(fx.Repo.List());
    }

    // ---- fixture + helpers -------------------------------------------------

    /// <summary>Per-test fixture: temp <c>ModsFolder</c> + a DI-resolved
    /// <see cref="IModImportService"/> + <see cref="IModRepository"/>.</summary>
    private sealed class ImportFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "curator-import-" + Guid.NewGuid());
        public string ModsFolder { get; }
        public string ProfilesBaseFolder { get; }
        public IModImportService Service { get; }
        public IModRepository Repo { get; }

        public ImportFixture()
        {
            ModsFolder = Path.Combine(TempRoot, "mods");
            ProfilesBaseFolder = Path.Combine(TempRoot, "profiles");
            Directory.CreateDirectory(TempRoot);

            var config = CuratorConfig.CreateDefault();
            config.ModsFolder = ModsFolder;
            // Isolate the profiles base under TempRoot so LinkFolder's containment
            // check compares against a throwaway dir, never the real app-data.
            config.ProfilesBaseFolder = ProfilesBaseFolder;
            _provider = new ServiceCollection()
                .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config })
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
            Service = _provider.GetRequiredService<IModImportService>();
            Repo = _provider.GetRequiredService<IModRepository>();
        }

        public IModRepository ReloadRepo()
        {
            var config = CuratorConfig.CreateDefault();
            config.ModsFolder = ModsFolder;
            var provider = new ServiceCollection()
                .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config })
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
            return provider.GetRequiredService<IModRepository>();
        }

        /// <summary>Creates a valid mod source folder <c>&lt;TempRoot&gt;/&lt;baseName&gt;</c>
        /// containing <c>&lt;baseName&gt;.mod</c> plus the given (relativePath, content)
        /// files, returning its full path.</summary>
        public string MakeSourceModFolder(string baseName, params (string Path, string Content)[] files)
        {
            var dir = Path.Combine(TempRoot, baseName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, baseName + ".mod"), baseName);
            foreach (var (relPath, content) in files)
            {
                var full = Path.Combine(dir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            return dir;
        }

        /// <summary>Creates a valid external mod folder <em>outside</em> the mods
        /// root (a sibling of <c>mods/</c> under <c>TempRoot</c>) shaped exactly
        /// as <see cref="LinkFolder"/> requires: the folder IS the base and
        /// directly contains <c>&lt;baseName&gt;.mod</c>. Returns its full path.
        /// Used by the linked-folder tests so the containment check passes.</summary>
        public string MakeExternalModFolder(string baseName, params (string Path, string Content)[] files) =>
            MakeSourceModFolder(baseName, files);

        /// <summary>Creates <c>&lt;TempRoot&gt;/&lt;dirName&gt;</c> with the given
        /// (relativePath, content) files (NO <c>.mod</c> descriptor), returning its
        /// full path. Used by tests that need a structurally-invalid source.</summary>
        public string MakeSourceFolder(string dirName, params (string Path, string Content)[] files)
        {
            var dir = Path.Combine(TempRoot, dirName);
            Directory.CreateDirectory(dir);
            foreach (var (relPath, content) in files)
            {
                var full = Path.Combine(dir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            return dir;
        }

        /// <summary>Builds a valid mod <c>.zip</c> at <paramref name="path"/> whose
        /// single top-level folder is <paramref name="baseName"/>, containing
        /// <c>&lt;baseName&gt;/&lt;baseName&gt;.mod</c> plus the given files (each placed
        /// under <c>&lt;baseName&gt;/</c>). Returns <paramref name="path"/>.</summary>
        public string MakeModZip(string path, string baseName, params (string Path, string Content)[] files)
        {
            var entries = new List<(string Entry, string Content)>
            {
                ($"{baseName}/{baseName}.mod", baseName),
            };
            entries.AddRange(files.Select(f => ($"{baseName}/{f.Path}", f.Content)));
            MakeZip(path, entries.ToArray());
            return path;
        }

        /// <summary>Builds a valid mod <c>.7z</c> at <paramref name="path"/> whose
        /// single top-level folder is <paramref name="baseName"/>, containing
        /// <c>&lt;baseName&gt;/&lt;baseName&gt;.mod</c> plus the given files (each placed
        /// under <c>&lt;baseName&gt;/</c>). Returns <paramref name="path"/>.</summary>
        public string MakeModSevenZip(string path, string baseName, params (string Path, string Content)[] files)
        {
            var entries = new List<(string Entry, string Content)>
            {
                ($"{baseName}/{baseName}.mod", baseName),
            };
            entries.AddRange(files.Select(f => ($"{baseName}/{f.Path}", f.Content)));
            MakeSevenZip(path, entries.ToArray());
            return path;
        }

        public void Dispose()
        {
            _provider.Dispose();
            if (Directory.Exists(TempRoot))
            {
                Directory.Delete(TempRoot, recursive: true);
            }
        }
    }

    /// <summary>Builds a raw <c>.zip</c> at <paramref name="path"/> from the given
    /// (entryPath, content) pairs, with no validation. Uses the BCL
    /// <see cref="ZipFile"/> (Create mode) which permits arbitrary entry keys
    /// (including <c>../</c> traversal), so it is used both for valid fixtures
    /// (via <c>MakeModZip</c>) and for structurally-invalid + malicious archives.
    /// </summary>
    private static void MakeZip(string path, params (string Entry, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var ze = archive.CreateEntry(entry);
            using var writer = new StreamWriter(ze.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    /// <summary>Builds a raw <c>.7z</c> at <paramref name="path"/> from the given
    /// (entryPath, content) pairs, with no validation. Uses SharpCompress's
    /// <see cref="WriterFactory"/> (LZMA2). Rar cannot be written programmatically
    /// (proprietary format); the committed <c>Fixtures/rarfixture.rar</c> covers it.
    /// </summary>
    private static void MakeSevenZip(string path, params (string Entry, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = WriterFactory.OpenWriter(
            stream, ArchiveType.SevenZip, new SevenZipWriterOptions(CompressionType.LZMA2));
        foreach (var (entry, content) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            using var ms = new MemoryStream(bytes);
            writer.Write(entry, ms, DateTime.UtcNow);
        }
    }
}
