using System.Text.Json;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Mods.Tests;

/// <summary>
/// <see cref="IModRepository"/>: container/version CRUD, <see cref="FindBySource"/>
/// per source-type + <see cref="FindUntrackedByName"/>, manifest round-trip +
/// in-memory index rebuild from a scan, <see cref="PruneUnreferenced"/> (drops the
/// right folders + empty containers), opaque version-folder naming, derived paths.
/// Resolves via DI (black-box) against a temp <c>ModsFolder</c>.
/// </summary>
public sealed class ModRepositoryTests
{
    // ---- list / get --------------------------------------------------------

    [Fact]
    public void List_is_empty_when_folder_is_empty()
    {
        using var fx = new RepoFixture();
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Get_returns_null_for_unknown_id()
    {
        using var fx = new RepoFixture();
        Assert.Null(fx.Repo.Get(Guid.NewGuid()));
    }

    [Fact]
    public void CreateContainer_assigns_a_non_empty_guid_and_writes_manifest()
    {
        using var fx = new RepoFixture();

        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        Assert.NotEqual(Guid.Empty, container.Id);
        Assert.Equal("DMF", container.Name);
        Assert.IsType<UntrackedSource>(container.Source);
        Assert.Empty(container.Versions);
        // The manifest is on disk.
        Assert.True(File.Exists(fx.ManifestPath(container.Id)));
    }

    [Fact]
    public void CreateContainer_rejects_null_or_whitespace_name()
    {
        using var fx = new RepoFixture();
        Assert.Throws<ArgumentException>(() => fx.Repo.CreateContainer(new UntrackedSource(), ""));
        Assert.Throws<ArgumentException>(() => fx.Repo.CreateContainer(new UntrackedSource(), "   "));
    }

    [Fact]
    public void Get_returns_the_created_container()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 42 }, "WT");

        var loaded = fx.Repo.Get(container.Id);
        Assert.NotNull(loaded);
        Assert.Equal(container.Id, loaded!.Id);
        Assert.Equal("WT", loaded.Name);
        Assert.Equal(42, Assert.IsType<NexusSource>(loaded.Source).ModId);
    }

    // ---- FindBySource / FindUntrackedByName -------------------------------

    [Fact]
    public void FindBySource_finds_Nexus_by_mod_id()
    {
        using var fx = new RepoFixture();
        var created = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");

        var found = fx.Repo.FindBySource(new NexusSource { ModId = 4242 });
        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
    }

    [Fact]
    public void FindBySource_returns_null_for_Untracked_and_for_unknown_sources()
    {
        // Untracked identity is the container Name; route through FindUntrackedByName.
        using var fx = new RepoFixture();
        fx.Repo.CreateContainer(new UntrackedSource(), "WT");

        Assert.Null(fx.Repo.FindBySource(new UntrackedSource()));
        Assert.Null(fx.Repo.FindBySource(new NexusSource { ModId = 1 }));
    }

    [Fact]
    public void FindUntrackedByName_finds_untracked_container_by_name_ordinal()
    {
        using var fx = new RepoFixture();
        var created = fx.Repo.CreateContainer(new UntrackedSource(), "WeaponTweaks");

        var found = fx.Repo.FindUntrackedByName("WeaponTweaks");
        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);

        Assert.Null(fx.Repo.FindUntrackedByName("weapontweaks")); // ordinal, case-sensitive
        Assert.Null(fx.Repo.FindUntrackedByName("Other"));
    }

    [Fact]
    public void Untracked_and_Nexus_with_same_name_do_not_collide()
    {
        // Different source-types are separate namespaces (goal #4: no collision
        // across sources).
        using var fx = new RepoFixture();
        var untracked = fx.Repo.CreateContainer(new UntrackedSource(), "WT");
        var nexus = fx.Repo.CreateContainer(new NexusSource { ModId = 99 }, "WT");

        Assert.NotEqual(untracked.Id, nexus.Id);
        Assert.Equal(untracked.Id, fx.Repo.FindUntrackedByName("WT")!.Id);
        Assert.Equal(nexus.Id, fx.Repo.FindBySource(new NexusSource { ModId = 99 })!.Id);
    }

    // ---- AddVersion (new + dedup + isLatest) ------------------------------

    [Fact]
    public void AddVersion_creates_opaque_folder_and_marks_isLatest()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        var updated = fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "marker.txt"), "1.0");
        });

        var version = Assert.Single(updated.Versions);
        Assert.Equal("1.0", version.VersionString);
        Assert.True(version.IsLatest);
        Assert.NotEmpty(version.Folder);
        // Opaque: the folder name is a hex GUID (32 chars, no dashes), not the
        // version tag.
        Assert.Matches("^[0-9a-f]{32}$", version.Folder);
        Assert.NotEqual("1.0", version.Folder);

        // Files landed in the derived version-folder path.
        var versionPath = fx.Repo.GetVersionFolderPath(container.Id, version.Folder);
        Assert.True(File.Exists(Path.Combine(versionPath, "marker.txt")));
    }

    [Fact]
    public void AddVersion_flips_isLatest_to_the_newest_import_when_all_versions_are_manual()
    {
        // All-null RemoteUploadedAt (manual imports): the arrival rule
        // degenerates to a plain ImportedAt argmax: the newest import carries
        // isLatest, exactly the pre-existing behavior.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        var v1 = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var v2 = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);

        // v2 is the newest arrival; it carries isLatest. v1's isLatest was cleared.
        Assert.True(v2.Versions.Single(v => v.VersionString == "2.0").IsLatest);
        Assert.False(v2.Versions.Single(v => v.VersionString == "1.0").IsLatest);
    }

    [Fact]
    public void AddVersion_with_same_versionString_dedups_reusing_the_folder()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        var first = fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "first");
        });
        var firstFolder = first.Versions.Single(v => v.VersionString == "1.0").Folder;

        // Re-import the same versionString: same folder, files refreshed, no new
        // version entry.
        var second = fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "b.txt"), "second");
        });

        var version = Assert.Single(second.Versions);
        Assert.Equal(firstFolder, version.Folder); // folder reused
        var versionPath = fx.Repo.GetVersionFolderPath(container.Id, version.Folder);
        Assert.False(File.Exists(Path.Combine(versionPath, "a.txt")), "re-import should clean + repopulate (no merge)");
        Assert.True(File.Exists(Path.Combine(versionPath, "b.txt")));
    }

    [Fact]
    public void AddVersion_dedup_failure_preserves_the_old_version_and_manifest()
    {
        // Core transactional invariant: a failed re-import (populateFolder
        // throws partway through extraction) must leave the OLD version's files
        // intact on disk and the manifest unchanged. The pre-transactional
        // implementation deleted the old version folder (CleanTarget) BEFORE
        // invoking populateFolder, so an extraction failure stranded a
        // manifest-referenced folder with no recovery (the startup prune only
        // reclaims containers no profile references). PopulateAtomically stages
        // into a temp + swaps on success, so the old version is never touched
        // until the new content is fully extracted.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        // First import of "1.0": the original content on disk.
        var first = fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "original");
        });
        var originalFolder = first.Versions.Single(v => v.VersionString == "1.0").Folder;
        var versionPath = fx.Repo.GetVersionFolderPath(container.Id, originalFolder);
        Assert.True(File.Exists(Path.Combine(versionPath, "a.txt")));

        // Re-import "1.0": populateFolder writes one file then simulates a
        // real extraction failure (CRC error, disk full, I/O, etc.). The
        // partial write goes into the TEMP, never reaching the version folder.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Repo.AddVersion(container.Id, "1.0", dir =>
            {
                File.WriteAllText(Path.Combine(dir, "partial.txt"), "partial");
                throw new InvalidOperationException("simulated extraction failure");
            }));
        Assert.Equal("simulated extraction failure", ex.Message);

        // The OLD version's files survived on disk: a.txt still there, the
        // partial.txt written into the temp never reached the version folder.
        Assert.True(File.Exists(Path.Combine(versionPath, "a.txt")),
            "A failed re-import must leave the old version's files intact.");
        Assert.False(File.Exists(Path.Combine(versionPath, "partial.txt")),
            "The failed temp write must not leak into the version folder.");

        // No .tmp.* orphan left under the container dir (the failure path
        // cleaned up its temp).
        var containerDir = Path.Combine(fx.Folder, container.Id.ToString());
        var leftoverTemps = Directory.EnumerateDirectories(containerDir)
            .Where(n => Path.GetFileName(n).Contains(".tmp."))
            .ToArray();
        Assert.Empty(leftoverTemps);

        // Manifest unchanged on disk: reload a fresh repo (reads container.json
        // from disk, not the in-memory index) + verify exactly one version "1.0"
        // with the original folder. Guards against any future failure-path code
        // that mutates the persisted manifest despite the populate throw.
        var reloaded = fx.Reload().Get(container.Id);
        Assert.NotNull(reloaded);
        var version = Assert.Single(reloaded!.Versions);
        Assert.Equal("1.0", version.VersionString);
        Assert.Equal(originalFolder, version.Folder);
    }

    [Fact]
    public void AddVersion_new_version_failure_leaves_no_folder_and_no_manifest_entry()
    {
        // Transactional invariant for the new-version branch: a populateFolder
        // failure must create nothing on disk and add no manifest entry. The
        // pre-transactional implementation pre-created versionDir and called
        // populateFolder on it, so a failure left an empty/partial folder on
        // disk (and the manifest write was reached only after populate, which
        // is why no entry was added, but the disk footprint was leaked).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Repo.AddVersion(container.Id, "1.0", dir =>
            {
                File.WriteAllText(Path.Combine(dir, "partial.txt"), "partial");
                throw new InvalidOperationException("simulated extraction failure");
            }));
        Assert.Equal("simulated extraction failure", ex.Message);

        // The container dir contains only container.json: no version folder
        // created, no .tmp.* orphan left.
        var containerDir = Path.Combine(fx.Folder, container.Id.ToString());
        var subdirs = Directory.EnumerateDirectories(containerDir).ToArray();
        Assert.Empty(subdirs);

        // Manifest has zero versions.
        var reloaded = fx.Repo.Get(container.Id);
        Assert.NotNull(reloaded);
        Assert.Empty(reloaded!.Versions);
    }

    [Fact]
    public void AddVersion_populateFolder_receives_an_existing_empty_dir_on_both_branches()
    {
        // New contract: populateFolder receives an EXISTING, EMPTY directory (a
        // temp staged by the repo). Replaces the prior band-aid regression test
        // (AddVersion_dedup_ensures_folder_exists_before_populate), which only
        // asserted existence. The contract is now stronger (empty too) because
        // the dir is a fresh temp, not the cleaned-but-reused version folder.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        // New-version branch: callback sees an existing, empty dir.
        fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Assert.True(Directory.Exists(dir),
                "populateFolder must receive an existing directory (new-version branch).");
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        });

        // Dedup branch: callback again sees an existing, empty dir (the temp,
        // not the old version folder with its prior a.txt).
        fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Assert.True(Directory.Exists(dir),
                "populateFolder must receive an existing directory (dedup branch).");
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
            File.WriteAllText(Path.Combine(dir, "b.txt"), "y");
        });
    }

    [Fact]
    public void AddVersion_propagates_the_original_exception_from_populateFolder()
    {
        // The repo must rethrow populateFolder's exception AS-IS (no swallowing,
        // no wrapping), so callers see the actual failure type + message. This
        // is what lets the UI surface the real cause (e.g. InvalidDataException
        // for a corrupt archive from ModImportService.ExtractArchive).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Repo.AddVersion(container.Id, "1.0", _ =>
                throw new InvalidOperationException("simulated extraction failure")));

        Assert.Equal("simulated extraction failure", ex.Message);
        // No wrapping: the propagated exception is exactly the one thrown.
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void AddVersion_sweeps_orphan_temp_dirs_left_by_a_prior_crashed_import()
    {
        // Crash-recovery: if the process dies between CreateDirectory(temp) and
        // the atomic Move, the temp is left as a <versionFolder>.tmp.<guid>
        // orphan under the container dir. The repo's index is built from
        // container.json (not by scanning version subfolders), so the orphan is
        // invisible to the index but occupies disk. SweepOrphanTemps deletes
        // any *.tmp.* directories at the start of each AddVersion call.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        // Simulate a crashed prior import: leave a recognizable orphan temp dir
        // under the container dir, plus a decoy non-tmp dir that must be left
        // alone.
        var containerDir = Path.Combine(fx.Folder, container.Id.ToString());
        var orphanPath = Path.Combine(containerDir, "deadbeef.tmp." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Combine(orphanPath, "partial.txt"), "partial");
        var decoyPath = Path.Combine(containerDir, "regular-folder");
        Directory.CreateDirectory(decoyPath);

        fx.Repo.AddVersion(container.Id, "1.0", dir =>
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x"));

        // The orphan was swept; the decoy is untouched.
        Assert.False(Directory.Exists(orphanPath), "Orphan .tmp.* dir must be swept by AddVersion.");
        Assert.True(Directory.Exists(decoyPath), "Non-tmp dirs must be left alone.");
    }

    [Fact]
    public void RebuildIndex_sweeps_orphan_temp_dirs_at_startup()
    {
        // Crash-recovery across containers: SweepOrphanTemps also runs during
        // RebuildIndex (construction/rescan), once per container dir. An orphan
        // left by a crashed import into container A is reclaimed at the next
        // index build even if container A is never re-imported (the per-AddVersion
        // sweep would otherwise never touch it). Without this, an orphan in a
        // never-re-imported container would linger on disk indefinitely.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        fx.Repo.AddVersion(container.Id, "1.0", dir =>
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x"));

        // Simulate a crashed prior import: leave an orphan temp + a decoy dir.
        var containerDir = Path.Combine(fx.Folder, container.Id.ToString());
        var orphanPath = Path.Combine(containerDir, "deadbeef.tmp." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Combine(orphanPath, "partial.txt"), "partial");
        var decoyPath = Path.Combine(containerDir, "regular-folder");
        Directory.CreateDirectory(decoyPath);

        // A fresh repo (construction = RebuildIndex) sweeps the orphan. NO
        // AddVersion call here, deliberately: this is the case the per-AddVersion
        // sweep cannot cover (the container is never re-imported).
        fx.Reload();

        Assert.False(Directory.Exists(orphanPath), "Startup RebuildIndex must sweep orphan .tmp.* dirs.");
        Assert.True(Directory.Exists(decoyPath), "Non-tmp dirs must be left alone.");
    }

    [Fact]
    public void AddVersion_throws_KeyNotFoundException_for_unknown_container()
    {
        using var fx = new RepoFixture();
        Assert.Throws<KeyNotFoundException>(() =>
            fx.Repo.AddVersion(Guid.NewGuid(), "1.0", EmptyPopulate));
    }

    // ---- AddVersion + RemoteUploadedAt (Nexus update-check basis) ---------

    [Fact]
    public void AddVersion_records_RemoteUploadedAt_on_a_new_version()
    {
        // The publish date forwarded by the acquisition layer is stamped on the
        // new entry (the basis for the update-check publish-date comparison).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var publishedAt = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);

        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, publishedAt);

        var version = Assert.Single(updated.Versions);
        Assert.Equal(publishedAt, version.RemoteUploadedAt);
    }

    [Fact]
    public void AddVersion_default_remoteUploadedAt_is_null()
    {
        // Existing callers (manual imports, profile fixture helpers) omit the
        // param; the entry's RemoteUploadedAt is null (the update check then
        // falls back to ImportedAt).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "Mod");

        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);

        var version = Assert.Single(updated.Versions);
        Assert.Null(version.RemoteUploadedAt);
    }

    [Fact]
    public void AddVersion_dedup_refreshes_RemoteUploadedAt_on_re_import()
    {
        // Re-importing the same VersionString refreshes the files AND
        // RemoteUploadedAt (matching how dedup refreshes files). A
        // re-acquired version carries the current publish date, not the stale
        // one from the first import, so a post-update check does not re-flag.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var first = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var refresh = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, first);
        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, refresh);

        var version = Assert.Single(updated.Versions);
        Assert.Equal(refresh, version.RemoteUploadedAt);
    }

    [Fact]
    public void AddVersion_persists_RemoteUploadedAt_through_a_new_repository_instance()
    {
        // Backward-compatible on disk: a nullable init-only property round-
        // trips through container.json. A pre-existing manifest (without the
        // field) deserializes the field to null; a manifest written with the
        // field preserves the value on reload.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var publishedAt = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, publishedAt);

        var reloaded = fx.Reload();
        var version = Assert.Single(reloaded.Get(container.Id)!.Versions);
        Assert.Equal(publishedAt, version.RemoteUploadedAt);
    }

    // ---- AddVersion + IsLatest by remote availability ----------------------

    [Fact]
    public void AddVersion_older_remote_file_does_not_flip_isLatest()
    {
        // The heart of the fix: the container holds v1.4 (remote-published
        // June); an import of v1.0 (remote-published March, a new
        // VersionString) must NOT steal isLatest even though it is the most
        // recently imported. Latest tracks the newest file the remote source
        // offers among the container's versions, not import recency.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var june = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var march = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.4", EmptyPopulate, june);
        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, march);

        Assert.True(updated.Versions.Single(v => v.VersionString == "1.4").IsLatest);
        Assert.False(updated.Versions.Single(v => v.VersionString == "1.0").IsLatest);
    }

    [Fact]
    public void AddVersion_newer_remote_file_flips_isLatest()
    {
        // Regression guard for the other direction: a genuinely newer remote
        // file takes the flag from an older remote-published entry.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var march = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var june = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, march);
        var updated = fx.Repo.AddVersion(container.Id, "1.4", EmptyPopulate, june);

        Assert.True(updated.Versions.Single(v => v.VersionString == "1.4").IsLatest);
        Assert.False(updated.Versions.Single(v => v.VersionString == "1.0").IsLatest);
    }

    // ---- AddVersion: the arrival rule on mixed containers --------------------
    //
    // The most recent ARRIVAL decides which clock governs: a manual import
    // (null RemoteUploadedAt) with the newest ImportedAt is latest; otherwise
    // the newest DOWNLOADED version by RemoteUploadedAt is latest (manual
    // imports ignored in that branch). All-manual and all-download containers
    // keep their pre-existing outcomes.

    [Fact]
    public void AddVersion_a_download_arriving_after_a_manual_import_takes_latest()
    {
        // THE bug: the manual v1.1.20 was imported today; the downloaded
        // v1.1.21 (published June, an older date than the manual's fresh
        // import stamp) arrives afterward. The most recent arrival is the
        // download, so the newest downloaded version is latest: the old
        // effective-timestamp comparison let the manual's fresh ImportedAt
        // outrank the download's publish date forever.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var june = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.1.20", EmptyPopulate); // manual, arrives first
        var updated = fx.Repo.AddVersion(container.Id, "1.1.21", EmptyPopulate, june);

        Assert.True(updated.Versions.Single(v => v.VersionString == "1.1.21").IsLatest);
        Assert.False(updated.Versions.Single(v => v.VersionString == "1.1.20").IsLatest);
    }

    [Fact]
    public void AddVersion_a_later_manual_import_lands_as_latest_on_a_downloaded_container()
    {
        // The migration case: a hand-imported folder landing on a previously
        // downloaded container becomes latest (the most recent arrival is the
        // manual import; the user just brought this content in by hand).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var june = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.1.21", EmptyPopulate, june);
        var updated = fx.Repo.AddVersion(container.Id, "migrated", EmptyPopulate); // manual, later

        Assert.True(updated.Versions.Single(v => v.VersionString == "migrated").IsLatest);
        Assert.False(updated.Versions.Single(v => v.VersionString == "1.1.21").IsLatest);
    }

    [Fact]
    public void AddVersion_an_older_remote_file_arriving_after_a_download_does_not_flip_latest()
    {
        // #232's original case must keep passing: among downloads, latest
        // tracks the newest publish date, not import recency. (Overlaps
        // AddVersion_older_remote_file_does_not_flip_isLatest; restated here
        // as the arrival rule's all-download row: the newest arrival is a
        // download, so the newest downloaded version by RemoteUploadedAt
        // wins.)
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var june = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var march = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.1.21", EmptyPopulate, june);
        var updated = fx.Repo.AddVersion(container.Id, "1.1.20", EmptyPopulate, march);

        Assert.True(updated.Versions.Single(v => v.VersionString == "1.1.21").IsLatest);
        Assert.False(updated.Versions.Single(v => v.VersionString == "1.1.20").IsLatest);
    }

    [Fact]
    public void AddVersion_downloads_only_rank_by_remote_date_with_the_arrival_stamp_breaking_ties()
    {
        // The all-download row, incl. the tie-break: two files published the
        // same day, imported in either order, the later arrival's stamp wins
        // the exact-tie comparison.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var march = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, march);
        var updated = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, march);

        Assert.True(updated.Versions.Single(v => v.VersionString == "2.0").IsLatest);
        Assert.False(updated.Versions.Single(v => v.VersionString == "1.0").IsLatest);
    }

    [Fact]
    public void AddVersion_a_manual_arriving_between_downloads_is_latest_until_the_next_download()
    {
        // The full decision table in one container: download, then a manual
        // (manual latest), then another download (the download branch takes
        // over: newest downloaded version by publish date).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var march = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var june = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var afterDownload = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, march);
        Assert.True(afterDownload.Versions.Single(v => v.VersionString == "1.0").IsLatest);

        var afterManual = fx.Repo.AddVersion(container.Id, "hand", EmptyPopulate);
        Assert.True(afterManual.Versions.Single(v => v.VersionString == "hand").IsLatest);

        var afterSecondDownload = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, june);
        Assert.True(afterSecondDownload.Versions.Single(v => v.VersionString == "2.0").IsLatest);
        Assert.False(afterSecondDownload.Versions.Single(v => v.VersionString == "hand").IsLatest);
        Assert.False(afterSecondDownload.Versions.Single(v => v.VersionString == "1.0").IsLatest);
    }

    [Fact]
    public void AddVersion_mixed_a_refreshed_remote_date_does_not_steal_latest_from_a_newer_manual_arrival()
    {
        // The dedup branch is not a new arrival: the reused entry keeps its
        // original import stamp, so refreshing its remote date (even to a
        // newer one) cannot outrank a manual import that arrived later. The
        // manual stays latest.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var june = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var august = new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, june); // download, arrives first
        var manual = fx.Repo.AddVersion(container.Id, "hand", EmptyPopulate); // manual, later
        Assert.True(manual.Versions.Single(v => v.VersionString == "hand").IsLatest);

        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, august); // dedup refresh

        Assert.True(updated.Versions.Single(v => v.VersionString == "hand").IsLatest);
        Assert.False(updated.Versions.Single(v => v.VersionString == "1.0").IsLatest);
    }

    [Fact]
    public void AddVersion_dedup_promotes_the_reused_entry_when_refreshed_remote_timestamp_is_newest()
    {
        // An author replacing file content under the same version tag can
        // make the reused entry newly newest: "1.0"'s refreshed publish date
        // (June) overtakes "2.0"'s (March), so the flag moves to "1.0". The
        // dedup branch re-evaluates latest after refreshing RemoteUploadedAt.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var jan = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var mar = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var jun = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, jan);
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, mar);
        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, jun);

        Assert.Equal(jun, updated.Versions.Single(v => v.VersionString == "1.0").RemoteUploadedAt);
        Assert.True(updated.Versions.Single(v => v.VersionString == "1.0").IsLatest);
        Assert.False(updated.Versions.Single(v => v.VersionString == "2.0").IsLatest);
    }

    [Fact]
    public void AddVersion_dedup_with_equal_remote_timestamp_keeps_the_later_imported_entry_latest()
    {
        // Tie-break: after the refresh both entries carry March; the
        // ImportedAt tie-break keeps the flag on "2.0" (imported after
        // "1.0"). An equal-timestamp re-import must not flip latest by
        // re-import alone.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var jan = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var mar = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, jan);
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, mar);
        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, mar);

        Assert.False(updated.Versions.Single(v => v.VersionString == "1.0").IsLatest);
        Assert.True(updated.Versions.Single(v => v.VersionString == "2.0").IsLatest);
    }

    [Fact]
    public void AddVersion_dedup_with_older_remote_timestamp_keeps_the_current_latest()
    {
        // The dedup branch re-evaluates but does not blindly promote: a
        // refresh older than the current latest's remote date changes
        // nothing.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var jan = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var feb = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var mar = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, jan);
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, mar);
        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, feb);

        Assert.False(updated.Versions.Single(v => v.VersionString == "1.0").IsLatest);
        Assert.True(updated.Versions.Single(v => v.VersionString == "2.0").IsLatest);
    }

    // ---- AddVersion + remoteFileId (exact remote-file identity) ------------

    [Fact]
    public void AddVersion_records_remoteFileId_on_a_new_version()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");

        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, null, 5820);

        var version = Assert.Single(updated.Versions);
        Assert.Equal(5820, version.FileId);
    }

    [Fact]
    public void AddVersion_default_remoteFileId_is_null()
    {
        // Existing callers (manual imports, profile fixture helpers) omit the
        // param; the entry's FileId is null.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "Mod");

        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);

        var version = Assert.Single(updated.Versions);
        Assert.Null(version.FileId);
    }

    [Fact]
    public void AddVersion_dedup_overwrites_remoteFileId_on_re_import()
    {
        // Mirroring RemoteUploadedAt semantics: the dedup branch overwrites
        // the reused entry's FileId, so the first re-acquisition backfills a
        // legacy entry (self-heal by attrition), and a manual re-import
        // (null) clears it.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, null, 100);
        var refreshed = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, null, 200);
        Assert.Equal(200, Assert.Single(refreshed.Versions).FileId);

        var cleared = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        Assert.Null(Assert.Single(cleared.Versions).FileId);
    }

    [Fact]
    public void AddVersion_persists_remoteFileId_through_a_new_repository_instance()
    {
        // The field round-trips through container.json (no migration; STJ
        // default for a missing nullable is null).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, null, 5820);

        var reloaded = fx.Reload();
        var version = Assert.Single(reloaded.Get(container.Id)!.Versions);
        Assert.Equal(5820, version.FileId);
    }

    [Fact]
    public void Old_manifest_without_FileId_deserializes_null()
    {
        // Backward compatibility: a manifest written before the field existed
        // has no FileId (and no RemoteUploadedAt) on its version entries.
        // Both load as null without any migration pass.
        using var fx = new RepoFixture();
        var id = Guid.NewGuid();
        var folder = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(fx.Folder, id.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            fx.ManifestPath(id),
            $$"""
            {
              "Id": "{{id}}",
              "Source": { "$kind": "nexus", "ModId": 4242 },
              "Name": "Legacy",
              "Versions": [
                {
                  "Folder": "{{folder}}",
                  "VersionString": "1.0",
                  "IsLatest": true,
                  "ImportedAt": "2024-01-01T00:00:00+00:00"
                }
              ]
            }
            """);

        var reloaded = fx.Reload();

        var version = Assert.Single(reloaded.Get(id)!.Versions);
        Assert.Null(version.FileId);
        Assert.Null(version.RemoteUploadedAt);
    }

    // ---- RemoveVersion -----------------------------------------------------

    [Fact]
    public void RemoveVersion_drops_folder_and_manifest_entry()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var folder = updated.Versions[0].Folder;

        fx.Repo.RemoveVersion(container.Id, folder);

        var reloaded = fx.Repo.Get(container.Id);
        Assert.Empty(reloaded!.Versions);
        Assert.False(Directory.Exists(fx.Repo.GetVersionFolderPath(container.Id, folder)));
    }

    [Fact]
    public void RemoveVersion_promotes_newest_remaining_to_isLatest()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var updated = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);
        var latestFolder = updated.Versions.Single(v => v.IsLatest).Folder;

        fx.Repo.RemoveVersion(container.Id, latestFolder);

        var reloaded = fx.Repo.Get(container.Id);
        var promoted = Assert.Single(reloaded!.Versions);
        Assert.Equal("1.0", promoted.VersionString);
        Assert.True(promoted.IsLatest);
    }

    [Fact]
    public void RemoveVersion_promotes_by_effective_timestamp_over_import_recency()
    {
        // Remove the newest remote-published entry ("3.0", September). Among
        // the survivors, "1.0" carries the second-newest remote date (June)
        // but was imported FIRST; "2.0" was imported later with an older
        // remote date (March). All survivors are downloads, so the arrival
        // rule's download branch applies: the newest downloaded version by
        // remote date ("1.0") wins; the old ImportedAt argmax would have
        // picked "2.0".
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var mar = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var jun = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var sep = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, jun);
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, mar);
        var latest = fx.Repo.AddVersion(container.Id, "3.0", EmptyPopulate, sep);
        Assert.Equal("3.0", latest.Versions.Single(v => v.IsLatest).VersionString);
        var latestFolder = latest.Versions.Single(v => v.IsLatest).Folder;

        fx.Repo.RemoveVersion(container.Id, latestFolder);

        var reloaded = fx.Repo.Get(container.Id);
        Assert.True(reloaded!.Versions.Single(v => v.VersionString == "1.0").IsLatest);
        Assert.False(reloaded!.Versions.Single(v => v.VersionString == "2.0").IsLatest);
    }

    [Fact]
    public void RemoveVersion_removing_the_latest_download_promotes_the_next_download()
    {
        // A mixed container whose newest arrival is a download: latest is the
        // newest downloaded version ("1.0", June). Removing it leaves the
        // manual + the March download; the newest surviving arrival is still
        // the March download (it arrived after the manual), so the download
        // branch promotes the next-newest downloaded version.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var mar = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var jun = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "hand", EmptyPopulate); // manual, arrives first
        var latest = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, jun); // latest
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, mar); // newest arrival
        Assert.Equal("1.0", latest.Versions.Single(v => v.IsLatest).VersionString);

        fx.Repo.RemoveVersion(container.Id, latest.Versions.Single(v => v.IsLatest).Folder);

        var reloaded = fx.Repo.Get(container.Id);
        Assert.True(reloaded!.Versions.Single(v => v.VersionString == "2.0").IsLatest);
        Assert.False(reloaded!.Versions.Single(v => v.VersionString == "hand").IsLatest);
    }

    [Fact]
    public void RemoveVersion_removing_the_newest_arrival_promotes_per_the_survivors_arrival_order()
    {
        // A manual that arrived after a download is latest; removing the
        // manual promotes the download (the newest surviving arrival is a
        // download, so the newest downloaded version wins).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var jun = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, jun);
        var manual = fx.Repo.AddVersion(container.Id, "hand", EmptyPopulate); // manual latest
        Assert.Equal("hand", manual.Versions.Single(v => v.IsLatest).VersionString);

        // Remove the manual (the newest arrival): the download is the newest
        // survivor + the only download, so it is promoted.
        fx.Repo.RemoveVersion(container.Id, manual.Versions.Single(v => v.IsLatest).Folder);
        Assert.True(fx.Repo.Get(container.Id)!.Versions.Single(v => v.VersionString == "1.0").IsLatest);
    }

    [Fact]
    public void RemoveVersion_is_idempotent_for_unknown_container_or_folder()
    {
        using var fx = new RepoFixture();
        // Unknown container: no-op, no throw.
        fx.Repo.RemoveVersion(Guid.NewGuid(), "whatever");

        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        // Unknown folder on a real container: no-op, no throw.
        fx.Repo.RemoveVersion(container.Id, "nonexistent");
        Assert.Empty(fx.Repo.Get(container.Id)!.Versions);
    }

    // ---- RenameContainer ---------------------------------------------------

    [Fact]
    public void RenameContainer_updates_the_name_and_persists_it_to_the_manifest()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 42 }, "Old Name");

        var updated = fx.Repo.RenameContainer(container.Id, "New Author Title");

        Assert.NotNull(updated);
        Assert.Equal("New Author Title", updated!.Name);
        // In-memory index reflects the new name.
        Assert.Equal("New Author Title", fx.Repo.Get(container.Id)!.Name);
        // The manifest on disk reflects the new name (reload reads container.json,
        // not the in-memory index).
        Assert.Equal("New Author Title", fx.Reload().Get(container.Id)!.Name);
    }

    [Fact]
    public void RenameContainer_keeps_identity_and_does_not_move_the_directory()
    {
        // Identity (Id) is unchanged + the on-disk container directory (keyed by
        // Id) does not move: only the Name field in the manifest changes.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 7 }, "Old");
        var dirBefore = Path.Combine(fx.Folder, container.Id.ToString());
        Assert.True(Directory.Exists(dirBefore));

        var updated = fx.Repo.RenameContainer(container.Id, "New");

        Assert.Equal(container.Id, updated!.Id);
        Assert.Equal(dirBefore, Path.Combine(fx.Folder, updated.Id.ToString()));
        Assert.True(Directory.Exists(dirBefore));
        Assert.True(File.Exists(fx.ManifestPath(container.Id)));
    }

    [Fact]
    public void RenameContainer_is_a_noop_when_the_name_already_matches()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Same");

        var result = fx.Repo.RenameContainer(container.Id, "Same");

        Assert.NotNull(result);
        Assert.Equal("Same", result!.Name);
        // The returned reference is the unchanged container (same name); no error.
        Assert.Equal("Same", fx.Repo.Get(container.Id)!.Name);
    }

    [Fact]
    public void RenameContainer_returns_null_for_an_unknown_container()
    {
        using var fx = new RepoFixture();

        Assert.Null(fx.Repo.RenameContainer(Guid.NewGuid(), "Whatever"));
    }

    [Fact]
    public void RenameContainer_is_ordinal_case_sensitive()
    {
        // The name comparison is ordinal; a case-only difference is a rename.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 11 }, "WeaponTweaks");

        var updated = fx.Repo.RenameContainer(container.Id, "weapontweaks");

        Assert.Equal("weapontweaks", updated!.Name);
        Assert.Equal("weapontweaks", fx.Repo.Get(container.Id)!.Name);
    }

    [Fact]
    public void RenameContainer_keeps_the_untracked_name_index_consistent()
    {
        // Renaming an untracked container must update the untracked-name dedup
        // index: FindUntrackedByName resolves the new name + NOT the old one.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "OldUntracked");

        fx.Repo.RenameContainer(container.Id, "NewUntracked");

        Assert.Null(fx.Repo.FindUntrackedByName("OldUntracked"));
        var found = fx.Repo.FindUntrackedByName("NewUntracked");
        Assert.NotNull(found);
        Assert.Equal(container.Id, found!.Id);
    }

    [Fact]
    public void RenameContainer_on_a_nexus_container_does_not_touch_untracked_dedup()
    {
        // Nexus identity is the mod id, not the name: renaming a Nexus container
        // must not register it in (or remove it from) the untracked-name index,
        // and FindBySource still resolves it by mod id.
        using var fx = new RepoFixture();
        var nexus = fx.Repo.CreateContainer(new NexusSource { ModId = 55 }, "Nexus Old");

        fx.Repo.RenameContainer(nexus.Id, "Nexus New");

        // Still resolvable by mod id (identity unchanged).
        var found = fx.Repo.FindBySource(new NexusSource { ModId = 55 });
        Assert.NotNull(found);
        Assert.Equal("Nexus New", found!.Name);
        // Never registered under either name in the untracked index.
        Assert.Null(fx.Repo.FindUntrackedByName("Nexus Old"));
        Assert.Null(fx.Repo.FindUntrackedByName("Nexus New"));
    }

    // ---- manifest round-trip + index rebuild ------------------------------

    // ---- DisplayMetadata: backward compat + round-trip --------------------

    [Fact]
    public void Old_manifest_without_DisplayMetadata_loads_null()
    {
        // Backward compatibility: a manifest written before this field existed
        // has no DisplayMetadata property. System.Text.Json's default for a
        // missing nullable property is null, so the container loads with null
        // metadata without any migration pass or schema version.
        using var fx = new RepoFixture();
        var id = Guid.NewGuid();
        var dir = Path.Combine(fx.Folder, id.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            fx.ManifestPath(id),
            $$"""
            {
              "$kind": "nexus",
              "Id": "{{id}}",
              "Name": "Legacy",
              "Source": { "$kind": "nexus", "ModId": 4242 },
              "Versions": []
            }
            """);

        var reloaded = fx.Reload();

        var container = reloaded.Get(id);
        Assert.NotNull(container);
        Assert.Null(container!.DisplayMetadata);
    }

    [Fact]
    public void DisplayMetadata_round_trips_through_a_new_repository_instance()
    {
        // A written DisplayMetadata survives a fresh repo reading the manifest
        // from disk (no migration; STJ round-trips the nested object).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        var metadata = new ModDisplayMetadata
        {
            Summary = "A short summary.",
            ThumbnailUrl = "https://example.com/thumb.png",
            IsAdultContent = true,
        };

        Assert.True(fx.Repo.TryInitializeDisplayMetadata(container.Id, metadata));

        var reloaded = fx.Reload();
        var found = reloaded.Get(container.Id);
        Assert.NotNull(found);
        Assert.Equal(metadata, found!.DisplayMetadata);
    }

    [Fact]
    public void Empty_DisplayMetadata_round_trips_distinct_from_null()
    {
        // The null / non-null distinction is load-bearing for the backfill
        // candidate selection. A non-null empty object (fetched-but-empty) must
        // round-trip as a non-null empty object, not collapse to null.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        var empty = new ModDisplayMetadata();

        Assert.True(fx.Repo.TryInitializeDisplayMetadata(container.Id, empty));

        var reloaded = fx.Reload();
        var found = reloaded.Get(container.Id);
        Assert.NotNull(found);
        Assert.NotNull(found!.DisplayMetadata);
        Assert.Equal(string.Empty, found.DisplayMetadata!.Summary);
        Assert.Null(found.DisplayMetadata.ThumbnailUrl);
        Assert.False(found.DisplayMetadata.IsAdultContent);
    }

    // ---- TryInitializeDisplayMetadata: missing-only atomic contract --------

    [Fact]
    public void TryInitializeDisplayMetadata_rejects_null_metadata()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        Assert.Throws<ArgumentNullException>(() =>
            fx.Repo.TryInitializeDisplayMetadata(container.Id, null!));
    }

    [Fact]
    public void TryInitializeDisplayMetadata_returns_false_for_unknown_id()
    {
        using var fx = new RepoFixture();
        var metadata = new ModDisplayMetadata { Summary = "x" };

        Assert.False(fx.Repo.TryInitializeDisplayMetadata(Guid.NewGuid(), metadata));
    }

    [Fact]
    public void TryInitializeDisplayMetadata_sets_and_persists_on_a_null_container()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        var metadata = new ModDisplayMetadata
        {
            Summary = "summary",
            ThumbnailUrl = "https://example.com/thumb.png",
        };

        Assert.True(fx.Repo.TryInitializeDisplayMetadata(container.Id, metadata));
        Assert.Equal(metadata, fx.Repo.Get(container.Id)!.DisplayMetadata);
        Assert.Equal(metadata, fx.Reload().Get(container.Id)!.DisplayMetadata);
    }

    [Fact]
    public void TryInitializeDisplayMetadata_returns_false_and_does_not_rewrite_when_metadata_already_equal()
    {
        // Missing-only: a value-equal existing metadata returns false with no
        // manifest rewrite. The manifest mtime is unchanged.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        var metadata = new ModDisplayMetadata
        {
            Summary = "summary",
            ThumbnailUrl = "https://example.com/thumb.png",
        };
        Assert.True(fx.Repo.TryInitializeDisplayMetadata(container.Id, metadata));

        var manifest = fx.ManifestPath(container.Id);
        var firstWrite = File.GetLastWriteTimeUtc(manifest);

        // A brief sleep ensures the mtime check has resolution to detect a
        // rewrite (FAT/EXT4 second-granularity mtimes).
        Thread.Sleep(1100);
        Assert.False(fx.Repo.TryInitializeDisplayMetadata(container.Id, metadata));
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(manifest)); // not rewritten
    }

    [Fact]
    public void TryInitializeDisplayMetadata_never_overwrites_a_different_existing_value()
    {
        // The atomic missing-only contract: a different existing value is never
        // overwritten. This is the TOCTOU guard the backfill relies on: a
        // concurrent writer (acquisition, another backfill, a manual edit) that
        // set metadata between the caller's Get and this call wins.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        var first = new ModDisplayMetadata { Summary = "original" };
        var different = new ModDisplayMetadata { Summary = "different", IsAdultContent = true };
        Assert.True(fx.Repo.TryInitializeDisplayMetadata(container.Id, first));

        Assert.False(fx.Repo.TryInitializeDisplayMetadata(container.Id, different));

        // The original value survives; the different value was not written.
        Assert.Equal("original", fx.Repo.Get(container.Id)!.DisplayMetadata!.Summary);
        Assert.False(fx.Repo.Get(container.Id)!.DisplayMetadata!.IsAdultContent);
        Assert.Equal("original", fx.Reload().Get(container.Id)!.DisplayMetadata!.Summary);
    }

    [Fact]
    public void TryInitializeDisplayMetadata_does_not_touch_source_or_versions()
    {
        // The setter mutates only DisplayMetadata: source identity, name, and
        // versions are unchanged (no incidental side effects on the aggregate).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);

        Assert.True(fx.Repo.TryInitializeDisplayMetadata(
            container.Id, new ModDisplayMetadata { Summary = "x" }));

        var reloaded = fx.Reload().Get(container.Id);
        Assert.NotNull(reloaded);
        Assert.IsType<NexusSource>(reloaded!.Source);
        Assert.Equal(4242, ((NexusSource)reloaded.Source).ModId);
        Assert.Equal("WT", reloaded.Name);
        Assert.Single(reloaded.Versions);
    }

    [Fact]
    public void TryInitializeDisplayMetadata_manifest_write_failure_leaves_in_memory_null_and_retryable()
    {
        // A WriteContainer failure (disk full, I/O error, permission denied)
        // must propagate (repository mutations normally throw on I/O failure)
        // AND leave the in-memory aggregate's DisplayMetadata null so the caller
        // can retry. The manifest is written BEFORE _byId is updated, so a throw
        // from WriteContainer never reaches the in-memory publish.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        Assert.Null(fx.Repo.Get(container.Id)!.DisplayMetadata);

        // Obstruct the manifest write deterministically: replace container.json
        // with a directory of the same name. File.WriteAllText then throws
        // because the path is a directory, not a file.
        var manifest = fx.ManifestPath(container.Id);
        File.Delete(manifest);
        Directory.CreateDirectory(manifest);

        var metadata = new ModDisplayMetadata { Summary = "should not land" };
        // The exception type varies by platform (IOException on some,
        // UnauthorizedAccessException on Linux when the path is a directory);
        // the test asserts any exception was thrown + the in-memory state.
        var ex = Record.Exception(() =>
            fx.Repo.TryInitializeDisplayMetadata(container.Id, metadata));
        Assert.NotNull(ex);

        // The in-memory aggregate still has null DisplayMetadata: the failed
        // write did not leak into _byId, so a retry is possible.
        Assert.Null(fx.Repo.Get(container.Id)!.DisplayMetadata);

        // Clean up the obstruction so the fixture's Dispose (a recursive
        // directory delete) is not confused by a directory named container.json
        // where it expects a file. Directory.Delete(recursive) handles it
        // regardless, but removing it here keeps the fixture's teardown path
        // simple and deterministic.
        if (Directory.Exists(manifest))
        {
            Directory.Delete(manifest, recursive: true);
        }
    }

    // ---- AddVersion + displayMetadata pass-through -------------------------

    [Fact]
    public void AddVersion_applies_non_null_metadata_on_a_new_version()
    {
        // A new version applies the metadata in the same manifest update as the
        // new entry: the returned container carries both the version and the
        // metadata, and a fresh repo observes both from disk.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        var metadata = new ModDisplayMetadata
        {
            Summary = "from acquisition",
            ThumbnailUrl = "https://example.com/thumb.png",
        };

        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, null, null, metadata);

        Assert.Equal(metadata, updated.DisplayMetadata);
        Assert.Equal(metadata, fx.Reload().Get(container.Id)!.DisplayMetadata);
    }

    [Fact]
    public void AddVersion_applies_non_null_metadata_on_a_dedup_re_import()
    {
        // Re-importing the same versionString applies the metadata in the same
        // manifest update as the dedup refresh (matching how dedup refreshes
        // files + RemoteUploadedAt).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var metadata = new ModDisplayMetadata { Summary = "fresh" };

        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, null, null, metadata);

        Assert.Equal(metadata, updated.DisplayMetadata);
        Assert.Equal(metadata, fx.Reload().Get(container.Id)!.DisplayMetadata);
    }

    [Fact]
    public void AddVersion_with_null_metadata_preserves_existing_metadata()
    {
        // Null metadata preserves the prior value, including on a manual re-
        // import (the default-argument path the folder/archive add flow takes).
        // This is the load-bearing guarantee that a re-import never erases a
        // prior Nexus acquisition or backfill.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        var metadata = new ModDisplayMetadata { Summary = "captured once" };
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, null, null, metadata);

        // Manual re-import (no metadata argument): prior metadata survives.
        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);

        Assert.Equal(metadata, updated.DisplayMetadata);
        Assert.Equal(metadata, fx.Reload().Get(container.Id)!.DisplayMetadata);
    }

    [Fact]
    public void AddVersion_null_metadata_leaves_a_null_container_at_null()
    {
        // The default-argument path on a container with no prior metadata leaves
        // it null (no spurious empty object fabricated). Distinct from a later
        // explicit set with an empty object.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");

        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);

        Assert.Null(updated.DisplayMetadata);
    }

    [Fact]
    public void AddVersion_populate_failure_preserves_prior_files_and_metadata()
    {
        // Transactional invariant extended to metadata: a populateFolder
        // failure rethrows before the manifest write, so the OLD version's
        // files + the prior metadata both survive intact on disk and in the
        // in-memory manifest.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        var metadata = new ModDisplayMetadata { Summary = "first" };
        var first = fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "original");
        }, null, null, metadata);
        var originalFolder = first.Versions.Single(v => v.VersionString == "1.0").Folder;
        var versionPath = fx.Repo.GetVersionFolderPath(container.Id, originalFolder);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Repo.AddVersion(container.Id, "1.0", dir =>
            {
                File.WriteAllText(Path.Combine(dir, "partial.txt"), "partial");
                throw new InvalidOperationException("simulated extraction failure");
                // No metadata argument: even a non-null one would not reach the
                // manifest write because populateFolder throws first.
            }));
        Assert.Equal("simulated extraction failure", ex.Message);

        // Old files survived.
        Assert.True(File.Exists(Path.Combine(versionPath, "a.txt")));
        Assert.False(File.Exists(Path.Combine(versionPath, "partial.txt")));
        // Prior metadata survived in memory + on disk.
        Assert.Equal(metadata, fx.Repo.Get(container.Id)!.DisplayMetadata);
        Assert.Equal(metadata, fx.Reload().Get(container.Id)!.DisplayMetadata);
    }

    // ---- manifest round-trip + index rebuild (existing) -------------------

    [Fact]
    public void Container_manifest_round_trips_through_a_new_repository_instance()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        fx.Repo.AddVersion(container.Id, "v1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        });

        var reloaded = fx.Reload();

        var found = reloaded.FindBySource(new NexusSource { ModId = 4242 });
        Assert.NotNull(found);
        Assert.Equal("WT", found!.Name);
        var version = Assert.Single(found.Versions);
        Assert.Equal("v1.0", version.VersionString);
        Assert.True(version.IsLatest);
        Assert.NotEmpty(version.Folder);
    }

    [Fact]
    public void Index_rebuild_from_scan_picks_up_all_containers()
    {
        // The repository must rebuild its in-memory index from a scan, not from a
        // single databank file. Multiple containers from a prior instance are
        // visible after a reload.
        using var fx = new RepoFixture();
        var c1 = fx.Repo.CreateContainer(new UntrackedSource(), "A");
        var c2 = fx.Repo.CreateContainer(new NexusSource { ModId = 1 }, "B");
        var c3 = fx.Repo.CreateContainer(new NexusSource { ModId = 2 }, "C");

        var reloaded = fx.Reload();

        Assert.Equal(3, reloaded.List().Count);
        Assert.NotNull(reloaded.Get(c1.Id));
        Assert.NotNull(reloaded.Get(c2.Id));
        Assert.NotNull(reloaded.Get(c3.Id));
    }

    [Fact]
    public void Index_rebuild_skips_non_container_dirs_and_corrupt_manifests()
    {
        using var fx = new RepoFixture();
        var good = fx.Repo.CreateContainer(new UntrackedSource(), "Good");

        // A non-guid dir under the root: ignored by the scan.
        Directory.CreateDirectory(Path.Combine(fx.Folder, "not-a-guid"));
        // A guid dir with a corrupt container.json: skipped (logged), not fatal.
        var badId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(fx.Folder, badId.ToString()));
        File.WriteAllText(fx.ManifestPath(badId), "{ this is not json");

        var reloaded = fx.Reload();

        var only = Assert.Single(reloaded.List());
        Assert.Equal(good.Id, only.Id);
    }

    [Fact]
    public void Container_manifest_is_utf8_json_with_kind_discriminators_no_bom()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(
            new NexusSource { ModId = 4242 },
            "WT");
        fx.Repo.AddVersion(container.Id, "1.2", EmptyPopulate);

        var raw = File.ReadAllText(fx.ManifestPath(container.Id));
        Assert.Contains("\"$kind\": \"nexus\"", raw);
        // No BOM.
        var bytes = File.ReadAllBytes(fx.ManifestPath(container.Id));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        // And it round-trips as a container.
        var reparsed = JsonSerializer.Deserialize<ModContainer>(raw)!;
        Assert.Equal(container.Id, reparsed.Id);
    }

    // ---- PruneUnreferenced -------------------------------------------------

    [Fact]
    public void Prune_drops_unreferenced_version_folders_keeps_referenced()
    {
        using var fx = new RepoFixture();
        var keep = fx.Repo.CreateContainer(new UntrackedSource(), "Keep");
        var keepVersion = fx.Repo.AddVersion(keep.Id, "1.0", EmptyPopulate);
        var drop = fx.Repo.CreateContainer(new UntrackedSource(), "Drop");
        var dropVersion = fx.Repo.AddVersion(drop.Id, "1.0", EmptyPopulate);

        fx.Repo.PruneUnreferenced(new HashSet<(Guid, string)>
        {
            (keep.Id, keepVersion.Versions[0].Folder),
        });

        // Kept container intact.
        Assert.NotNull(fx.Repo.Get(keep.Id));
        Assert.True(Directory.Exists(fx.Repo.GetVersionFolderPath(keep.Id, keepVersion.Versions[0].Folder)));
        // Drop container had only the unreferenced version; it is removed entirely
        // (empty after the prune).
        Assert.Null(fx.Repo.Get(drop.Id));
        Assert.False(Directory.Exists(Path.Combine(fx.Folder, drop.Id.ToString())));
    }

    [Fact]
    public void Prune_removes_empty_containers()
    {
        // A container with zero versions after the prune is removed entirely
        // (manifest + dir).
        using var fx = new RepoFixture();
        var empty = fx.Repo.CreateContainer(new UntrackedSource(), "Empty");

        fx.Repo.PruneUnreferenced(new HashSet<(Guid, string)>());

        Assert.Null(fx.Repo.Get(empty.Id));
        Assert.False(Directory.Exists(Path.Combine(fx.Folder, empty.Id.ToString())));
    }

    [Fact]
    public void Prune_keeps_a_version_when_at_least_one_profile_references_it()
    {
        // Two versions on one container; one referenced, one not. Only the
        // unreferenced one is dropped; the container survives.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        var v1 = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var v2 = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);
        var v1Folder = v1.Versions.Single(v => v.VersionString == "1.0").Folder;
        var v2Folder = v2.Versions.Single(v => v.VersionString == "2.0").Folder;

        // Reference v1 only (e.g. a profile pinned to "1.0"). v2 (the latest) is
        // unreferenced and dropped.
        fx.Repo.PruneUnreferenced(new HashSet<(Guid, string)>
        {
            (container.Id, v1Folder),
        });

        var reloaded = fx.Repo.Get(container.Id);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.Versions);
        Assert.Equal("1.0", reloaded.Versions[0].VersionString);
        Assert.True(reloaded.Versions[0].IsLatest); // promoted on the removal of v2.
        Assert.False(Directory.Exists(fx.Repo.GetVersionFolderPath(container.Id, v2Folder)));
    }

    // ---- LinkedSource: FindBySource + IsExternalAvailable ------------------

    [Fact]
    public void FindBySource_finds_Linked_by_normalized_external_path()
    {
        using var fx = new RepoFixture();
        var external = Path.Combine(fx.Folder, "ExternalMod");
        Directory.CreateDirectory(external);
        var created = fx.Repo.CreateContainer(
            new LinkedSource { ExternalPath = Path.GetFullPath(external) },
            "ExternalMod");

        // Same normalized path resolves to the same container.
        var found = fx.Repo.FindBySource(new LinkedSource { ExternalPath = Path.GetFullPath(external) });
        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
    }

    [Fact]
    public void FindBySource_Linked_normalizes_both_sides_before_comparing()
    {
        using var fx = new RepoFixture();
        var external = Path.Combine(fx.Folder, "ExternalMod");
        Directory.CreateDirectory(external);
        var normalized = Path.GetFullPath(external);
        fx.Repo.CreateContainer(new LinkedSource { ExternalPath = normalized }, "ExternalMod");

        // A non-normalized input with a trailing separator + a relative segment
        // still resolves: GetFullPath canonicalizes both sides.
        var messy = external + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar;
        var found = fx.Repo.FindBySource(new LinkedSource { ExternalPath = messy });
        Assert.NotNull(found);
        Assert.Equal(normalized, Assert.IsType<LinkedSource>(found!.Source).ExternalPath);
    }

    [Fact]
    public void FindBySource_Linked_matches_platform_case_sensitivity()
    {
        // On Linux the path comparison is ordinal (case-sensitive); on Windows
        // it is case-insensitive (drive-letter case). The test asserts the
        // exact-ordinal arm always matches; the case-opposite arm matches iff
        // the platform comparison is case-insensitive.
        using var fx = new RepoFixture();
        var external = Path.Combine(fx.Folder, "MixedCaseMod");
        Directory.CreateDirectory(external);
        fx.Repo.CreateContainer(
            new LinkedSource { ExternalPath = Path.GetFullPath(external) },
            "MixedCaseMod");

        Assert.NotNull(fx.Repo.FindBySource(
            new LinkedSource { ExternalPath = Path.GetFullPath(external) }));

        var caseFlipped = OperatingSystem.IsWindows()
            ? Path.GetFullPath(external).ToUpperInvariant()
            : Path.GetFullPath(external).ToLowerInvariant();
        // Only matches when the platform comparison is case-insensitive.
        Assert.Equal(
            OperatingSystem.IsWindows(),
            fx.Repo.FindBySource(new LinkedSource { ExternalPath = caseFlipped }) is not null);
    }

    [Fact]
    public void FindBySource_Linked_returns_null_for_an_unlinked_path()
    {
        using var fx = new RepoFixture();
        var external = Path.Combine(fx.Folder, "Standalone");
        Directory.CreateDirectory(external);

        Assert.Null(fx.Repo.FindBySource(
            new LinkedSource { ExternalPath = Path.GetFullPath(external) }));
    }

    [Fact]
    public void IsExternalAvailable_returns_true_for_managed_and_unknown_ids()
    {
        // Default-safe contract: managed containers + unknown ids report
        // available so the UI never sees a false broken signal.
        using var fx = new RepoFixture();
        var managed = fx.Repo.CreateContainer(new UntrackedSource(), "Managed");
        fx.Repo.AddVersion(managed.Id, "1.0", EmptyPopulate);

        Assert.True(fx.Repo.IsExternalAvailable(managed.Id));
        Assert.True(fx.Repo.IsExternalAvailable(Guid.NewGuid())); // unknown
    }

    [Fact]
    public void IsExternalAvailable_tracks_linked_external_folder_presence()
    {
        // Availability is seeded when the container is recorded and recomputed
        // when the index is rebuilt (construction). Drive the rebuild by
        // constructing a second repository over the same mods folder.
        using var fx = new RepoFixture();
        var external = Path.Combine(fx.Folder, "LinkedMod");
        Directory.CreateDirectory(external);
        var container = fx.Repo.CreateContainer(
            new LinkedSource { ExternalPath = Path.GetFullPath(external) },
            "LinkedMod");

        // Available while the folder exists.
        Assert.True(fx.Repo.IsExternalAvailable(container.Id));

        // Missing folder: a fresh index build over the same root sees it gone.
        Directory.Delete(external);
        var missingView = new ModRepository(fx.ConfigLoader, NullLogger<ModRepository>.Instance);
        Assert.False(missingView.IsExternalAvailable(container.Id));

        // Missing-then-returned: the next index build sees it back.
        Directory.CreateDirectory(external);
        var returnedView = new ModRepository(fx.ConfigLoader, NullLogger<ModRepository>.Instance);
        Assert.True(returnedView.IsExternalAvailable(container.Id));
    }

    [Fact]
    public void Index_rebuild_leaves_the_linked_external_target_untouched()
    {
        // Building the index enumerates linked containers but must never read
        // beyond or write into the external target.
        using var fx = new RepoFixture();
        var external = Path.Combine(fx.Folder, "LinkedMod");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "LinkedMod.mod"), "LinkedMod");
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var container = fx.Repo.CreateContainer(
            new LinkedSource { ExternalPath = Path.GetFullPath(external) },
            "LinkedMod");

        var fresh = new ModRepository(fx.ConfigLoader, NullLogger<ModRepository>.Instance);
        Assert.NotNull(fresh.Get(container.Id));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    // ---- LinkedSource: PruneUnreferenced keep/drop -------------------------

    [Fact]
    public void Prune_keeps_a_referenced_linked_container_even_with_zero_versions()
    {
        // The critical fix: a linked container in a profile must survive the
        // startup prune even though it has zero versions, because the caller
        // marks it referenced by containerId (the empty-string version folder
        // sentinel).
        using var fx = new RepoFixture();
        var external = Path.Combine(fx.Folder, "LinkedMod");
        Directory.CreateDirectory(external);
        var linked = fx.Repo.CreateContainer(
            new LinkedSource { ExternalPath = Path.GetFullPath(external) },
            "LinkedMod");

        fx.Repo.PruneUnreferenced(new HashSet<(Guid, string)> { (linked.Id, string.Empty) });

        Assert.NotNull(fx.Repo.Get(linked.Id));
        Assert.True(Directory.Exists(Path.Combine(fx.Folder, linked.Id.ToString())));
        // External folder untouched.
        Assert.True(Directory.Exists(external));
    }

    [Fact]
    public void Prune_drops_an_unreferenced_linked_container_but_never_the_external_target()
    {
        using var fx = new RepoFixture();
        var external = Path.Combine(fx.Folder, "LinkedMod");
        // A sentinel marker inside the external target proves Curator never
        // touches the user's folder during the prune.
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");

        var linked = fx.Repo.CreateContainer(
            new LinkedSource { ExternalPath = Path.GetFullPath(external) },
            "LinkedMod");

        // Unreferenced (empty referenced set): the container is pruned.
        fx.Repo.PruneUnreferenced(new HashSet<(Guid, string)>());

        Assert.Null(fx.Repo.Get(linked.Id));
        Assert.False(Directory.Exists(Path.Combine(fx.Folder, linked.Id.ToString())));
        // External target + its sentinel survive intact.
        Assert.True(Directory.Exists(external));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Prune_still_removes_a_managed_container_with_zero_versions()
    {
        // Regression guard: the linked-aware prune must not accidentally keep a
        // managed zero-version container (those remain garbage).
        using var fx = new RepoFixture();
        var managed = fx.Repo.CreateContainer(new UntrackedSource(), "Empty");
        // No versions, not referenced.
        fx.Repo.PruneUnreferenced(new HashSet<(Guid, string)>());
        Assert.Null(fx.Repo.Get(managed.Id));
    }

    [Fact]
    public void Index_rebuild_skips_an_unknown_source_kind_gracefully()
    {
        // Forward-compat: a manifest whose Source carries a $kind this build
        // does not know (IgnoreUnrecognizedTypeDiscriminators = false ->
        // JsonException on the nested Source) is skipped, not fatal to the rest
        // of the index. The $kind discriminator lives on the Source field (the
        // polymorphism root), not at the container top level.
        using var fx = new RepoFixture();
        var good = fx.Repo.CreateContainer(new UntrackedSource(), "Good");
        var unknownId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(fx.Folder, unknownId.ToString()));
        File.WriteAllText(
            fx.ManifestPath(unknownId),
            "{\"Id\":\"" + unknownId + "\",\"Source\":{\"$kind\":\"future_source\"},\"Name\":\"Future\"}");

        var reloaded = fx.Reload();

        var only = Assert.Single(reloaded.List());
        Assert.Equal(good.Id, only.Id);
    }

    // ---- DI registration ---------------------------------------------------

    [Fact]
    public void AddMods_registers_resolvable_IModRepository()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IConfigLoader>(new FakeConfigLoader())
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddMods()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<IModRepository>());
    }

    // ---- DirectoryCopy -----------------------------------------------------

    [Fact]
    public void DirectoryCopy_reproduces_the_source_tree_and_leaves_the_source_intact()
    {
        // The shared helper behind the folder import: a faithful recursive
        // copy. Files + nested subdirs land at the target exactly; the source
        // is left intact (the caller is responsible for any delete).
        using var fx = new RepoFixture();
        var source = Path.Combine(fx.Folder, "src");
        Directory.CreateDirectory(Path.Combine(source, "sub", "deep"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "b");
        File.WriteAllText(Path.Combine(source, "sub", "deep", "c.txt"), "c");

        var target = Path.Combine(fx.Folder, "dst");

        DirectoryCopy.Copy(source, target);

        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
        Assert.True(File.Exists(Path.Combine(target, "sub", "b.txt")));
        Assert.True(File.Exists(Path.Combine(target, "sub", "deep", "c.txt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("c", File.ReadAllText(Path.Combine(target, "sub", "deep", "c.txt")));
        // Source untouched (copy does not delete).
        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
        Assert.True(File.Exists(Path.Combine(source, "sub", "deep", "c.txt")));
    }

    // ---- fixture + helpers -------------------------------------------------

    private static readonly Action<string> EmptyPopulate = dir =>
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
    };

    /// <summary>Per-test fixture: temp <c>ModsFolder</c> + a DI-resolved
    /// <see cref="IModRepository"/>.</summary>
    private sealed class RepoFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "curator-repo-" + Guid.NewGuid());
        public IModRepository Repo { get; }

        /// <summary>
        /// The live <see cref="FakeConfigLoader"/> the repository reads
        /// <c>ModsFolder</c> from. Exposed so tests can re-read the persisted
        /// state.
        /// </summary>
        public FakeConfigLoader ConfigLoader => _configLoader;

        private readonly FakeConfigLoader _configLoader;

        /// <summary>
        /// The live <see cref="CuratorConfig"/> the repository reads
        /// <c>ModsFolder</c> from.
        /// </summary>
        public CuratorConfig Config => _configLoader.Config;

        public RepoFixture()
        {
            var config = CuratorConfig.CreateDefault();
            config.ModsFolder = Folder;
            _configLoader = new FakeConfigLoader { Config = config };

            // Production DI path: AddMods() resolves ModRepository via its
            // public ctor.
            _provider = new ServiceCollection()
                .AddSingleton<IConfigLoader>(_configLoader)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
            Repo = _provider.GetRequiredService<IModRepository>();
        }

        public string ManifestPath(Guid containerId) =>
            Path.Combine(Folder, containerId.ToString(), "container.json");

        public IModRepository Reload()
        {
            var config = CuratorConfig.CreateDefault();
            config.ModsFolder = Folder;
            var provider = new ServiceCollection()
                .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config })
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
            // Transient provider: tests are short-lived; the process exits before
            // disposal matters (matches the existing Profiles fixture posture).
            return provider.GetRequiredService<IModRepository>();
        }

        public void Dispose()
        {
            _provider.Dispose();
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
    }
}
