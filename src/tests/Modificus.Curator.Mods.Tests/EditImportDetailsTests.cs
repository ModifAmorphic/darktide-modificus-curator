using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Mods.Tests;

/// <summary>
/// <see cref="IModRepository.EditImportDetails"/>: every edit branch (rename,
/// same-identity retag, Untracked to Nexus, the Nexus-unknown retag, the
/// identity reset with older-version removal, Nexus to Untracked), the
/// downloaded-not-editable refusal (a version carrying a FileId OR a
/// RemoteUploadedAt grounds the container; every edit refused, name-only
/// included; both evidence shapes), the Untracked-only name rule, the
/// removeOlderVersions guard, the tag-collision refusal, the
/// duplicate-identity guard, and the untracked-name index coherence across an
/// Untracked rename + a Nexus-to-Untracked swap. Resolves via DI (black-box)
/// against a temp <c>ModsFolder</c>, the established repository test style.
/// </summary>
public sealed class EditImportDetailsTests
{
    private static readonly DateTimeOffset OldStamp =
        new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- lookup + argument guards -------------------------------------------

    [Fact]
    public void Unknown_container_id_returns_null()
    {
        using var fx = new RepoFixture();
        Assert.Null(fx.Repo.EditImportDetails(
            Guid.NewGuid(), "Name", new UntrackedSource(), "", removeOlderVersions: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Whitespace_name_throws(string name)
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "Old");

        Assert.Throws<ArgumentException>(() => fx.Repo.EditImportDetails(
            container.Id, name, new UntrackedSource(), "", removeOlderVersions: false));
    }

    [Fact]
    public void Linked_source_argument_throws()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "Old");

        Assert.Throws<ArgumentException>(() => fx.Repo.EditImportDetails(
            container.Id, "New", new LinkedSource { ExternalPath = "/tmp/x" }, "",
            removeOlderVersions: false));
    }

    [Fact]
    public void Linked_container_cannot_be_edited()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(
            new LinkedSource { ExternalPath = "/tmp/external" }, "External");

        Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "New", new UntrackedSource(), "", removeOlderVersions: false));
    }

    [Fact]
    public void Nexus_identity_owned_by_another_container_throws()
    {
        // One container per (source, identity): the edit must not fork the
        // invariant Import + FindExistingContainer maintain.
        using var fx = new RepoFixture();
        fx.Repo.CreateContainer(new NexusSource { ModId = 42 }, "Owner");
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "Local");

        var ex = Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "Local", new NexusSource { ModId = 42 }, "1.0",
            removeOlderVersions: false));
        Assert.Contains("42", ex.Message);
    }

    // ---- same-identity edits --------------------------------------------------

    [Fact]
    public void A_name_change_on_a_Nexus_container_is_refused()
    {
        // The name is Untracked-only: a Nexus mod's name comes from Nexus
        // (the update check's name-sync renames the container when Nexus's
        // name changes, so a user-typed name would be reverted). The refusal
        // leaves nothing mutated.
        using var fx = new RepoFixture();
        var container = SeedNexus(fx, "1.0");

        var ex = Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "Renamed", new NexusSource { ModId = 8 }, "1.0",
            removeOlderVersions: false));
        Assert.Contains("managed by Nexus", ex.Message);

        var unchanged = fx.Repo.Get(container.Id)!;
        Assert.Equal("WT", unchanged.Name);
        Assert.Single(unchanged.Versions);
    }

    [Fact]
    public void An_unchanged_name_on_a_Nexus_edit_is_allowed()
    {
        using var fx = new RepoFixture();
        var container = SeedNexus(fx, "1.0");

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 8 }, "1.0-hotfix",
            removeOlderVersions: false);

        Assert.NotNull(updated);
        Assert.Equal("WT", updated!.Name);
        Assert.Equal("1.0-hotfix", Assert.Single(updated.Versions).VersionString);
    }

    [Fact]
    public void A_rename_alongside_the_switch_to_Untracked_is_allowed()
    {
        // The name rule follows the DESTINATION: switching to Untracked makes
        // the name the identity again, so a rename with the switch is legal.
        using var fx = new RepoFixture();
        var container = SeedNexus(fx, "1.0");

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT Local", new UntrackedSource(), "",
            removeOlderVersions: false);

        Assert.NotNull(updated);
        Assert.Equal("WT Local", updated!.Name);
        Assert.IsType<UntrackedSource>(updated.Source);
        Assert.Equal(container.Id, fx.Repo.FindUntrackedByName("WT Local")!.Id);
    }

    [Fact]
    public void Untracked_rename_keeps_the_name_index_coherent()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "WT");
        fx.Repo.AddVersion(container.Id, "", EmptyPopulate);

        fx.Repo.EditImportDetails(
            container.Id, "WT Renamed", new UntrackedSource(), "",
            removeOlderVersions: false);

        Assert.Null(fx.Repo.FindUntrackedByName("WT"));
        Assert.Equal(container.Id, fx.Repo.FindUntrackedByName("WT Renamed")!.Id);
    }

    [Fact]
    public void Same_identity_retag_updates_only_the_latest_version_record()
    {
        using var fx = new RepoFixture();
        var container = SeedNexus(fx, "1.0");
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 8 }, "2.0-hotfix",
            removeOlderVersions: false);

        // The latest ("2.0") is retagged; the older version keeps its tag; no
        // version was removed (a same-identity tag edit never removes).
        Assert.NotNull(updated);
        Assert.Equal(2, updated!.Versions.Count);
        Assert.Contains(updated.Versions, v => v.VersionString == "2.0-hotfix" && v.IsLatest);
        Assert.Contains(updated.Versions, v => v.VersionString == "1.0" && !v.IsLatest);
    }

    [Fact]
    public void Retag_colliding_with_another_version_throws()
    {
        // The tag is the AddVersion upsert key: a retag onto a tag another
        // version on the same container already holds is the same conflict.
        using var fx = new RepoFixture();
        var container = SeedNexus(fx, "1.0");
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);

        Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 8 }, "1.0",
            removeOlderVersions: false));
    }

    // ---- Untracked -> Nexus ---------------------------------------------------

    [Fact]
    public void Untracked_to_Nexus_sets_source_and_tags_the_latest_version()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "WT");
        var stamped = fx.Repo.AddVersion(container.Id, "", EmptyPopulate);

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 123 }, "1.4",
            removeOlderVersions: false);

        Assert.NotNull(updated);
        Assert.Equal(123, Assert.IsType<NexusSource>(updated!.Source).ModId);
        var version = Assert.Single(updated.Versions);
        Assert.Equal("1.4", version.VersionString);
        // Local facts untouched: same folder, same import stamp, same effective
        // ordering key. The initial association fabricates no remote claims.
        Assert.Equal(stamped.Versions[0].Folder, version.Folder);
        Assert.Equal(stamped.Versions[0].ImportedAt, version.ImportedAt);
        Assert.Equal(stamped.Versions[0].RemoteUploadedAt, version.RemoteUploadedAt);
        Assert.Null(version.FileId);
    }

    [Fact]
    public void Untracked_to_Nexus_allows_an_empty_tag_for_the_association_path()
    {
        // The programmatic association path records identity without a version
        // stamp; the empty tag is the derived version-unknown state.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "WT");
        fx.Repo.AddVersion(container.Id, "", EmptyPopulate);

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 123 }, "",
            removeOlderVersions: false);

        Assert.NotNull(updated);
        Assert.Equal(string.Empty, Assert.Single(updated!.Versions).VersionString);
    }

    [Fact]
    public void Untracked_to_Nexus_moves_the_untracked_name_index()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "WT");
        fx.Repo.AddVersion(container.Id, "", EmptyPopulate);

        fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 123 }, "1.4",
            removeOlderVersions: false);

        // The container left the untracked namespace: the old name key is gone
        // + the Nexus identity is findable.
        Assert.Null(fx.Repo.FindUntrackedByName("WT"));
        Assert.Equal(container.Id, fx.Repo.FindBySource(new NexusSource { ModId = 123 })!.Id);
    }

    // ---- Nexus-unknown retag ---------------------------------------------------

    [Fact]
    public void Nexus_unknown_latest_is_retagged()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        fx.Repo.AddVersion(container.Id, "", EmptyPopulate);

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 8 }, "1.2",
            removeOlderVersions: false);

        Assert.NotNull(updated);
        Assert.Equal("1.2", Assert.Single(updated!.Versions).VersionString);
    }

    // ---- downloaded mods are not editable --------------------------------------

    [Fact]
    public void A_FileId_grounded_container_refuses_every_edit_including_name_only()
    {
        // Any version carrying a FileId grounds the whole container: there is
        // no degraded editing surface for a downloaded mod, not even the name.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, OldStamp, remoteFileId: 9001);

        // Name-only, same identity, unchanged tag: refused.
        var ex = Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT Fixed", new NexusSource { ModId = 8 }, "1.0",
            removeOlderVersions: false));
        Assert.Contains("downloaded from Nexus", ex.Message);

        // An id change + a source switch are refused identically (the same
        // guard; nothing was mutated by the first refusal).
        Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 9 }, "1.0",
            removeOlderVersions: false));
        Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT", new UntrackedSource(), "",
            removeOlderVersions: false));

        var unchanged = fx.Repo.Get(container.Id)!;
        Assert.Equal("WT", unchanged.Name);
        Assert.Equal(8, Assert.IsType<NexusSource>(unchanged.Source).ModId);
        Assert.Single(unchanged.Versions);
    }

    [Fact]
    public void A_RemoteUploadedAt_grounded_container_refuses_identically()
    {
        // The legacy shape: downloads from before FileId persistence carry
        // only the timestamp, and the timestamp is equally download evidence
        // (only the download path ever records either fact).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, remoteUploadedAt: OldStamp);

        var ex = Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT Fixed", new NexusSource { ModId = 8 }, "1.0",
            removeOlderVersions: false));
        Assert.Contains("downloaded from Nexus", ex.Message);
    }

    [Fact]
    public void An_older_grounded_version_refuses_even_when_the_latest_is_clean()
    {
        // Grounding reads EVERY version: a hand-imported copy landing on a
        // downloaded container does not un-ground it.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, OldStamp, remoteFileId: 9001);
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);

        Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 8 }, "2.0-hotfix",
            removeOlderVersions: false));
    }

    // ---- the identity reset ------------------------------------------------------

    [Fact]
    public void A_non_empty_tag_with_an_untracked_destination_throws()
    {
        // An untracked container is single-version by construction (the empty
        // tag is the upsert key re-imports dedupe onto); a non-empty tag would
        // fabricate a version record that contract cannot hold.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "WT");
        fx.Repo.AddVersion(container.Id, "", EmptyPopulate);

        Assert.Throws<ArgumentException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT", new UntrackedSource(), "1.0", removeOlderVersions: false));
    }

    [Fact]
    public void A_zero_version_container_edits_name_and_source_with_no_version_records()
    {
        // The latest-is-null branch: a container created but never given a
        // version edits its name + source cleanly; the tag has no version
        // record to land on and none is fabricated. The Nexus destination
        // keeps its name (the Untracked-only name rule).
        using var fx = new RepoFixture();
        var nexus = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        var untracked = fx.Repo.CreateContainer(new UntrackedSource(), "Local");

        var updatedNexus = fx.Repo.EditImportDetails(
            nexus.Id, "WT", new NexusSource { ModId = 9 }, "1.0",
            removeOlderVersions: false);
        var updatedUntracked = fx.Repo.EditImportDetails(
            untracked.Id, "Local Renamed", new UntrackedSource(), "",
            removeOlderVersions: false);

        Assert.NotNull(updatedNexus);
        Assert.Empty(updatedNexus!.Versions);
        Assert.Equal("WT", updatedNexus.Name);
        Assert.Equal(9, Assert.IsType<NexusSource>(updatedNexus.Source).ModId);
        Assert.NotNull(updatedUntracked);
        Assert.Empty(updatedUntracked!.Versions);
        Assert.Equal("Local Renamed", updatedUntracked.Name);
        Assert.Null(fx.Repo.FindUntrackedByName("Local"));
        Assert.Equal(untracked.Id, fx.Repo.FindUntrackedByName("Local Renamed")!.Id);
    }

    [Fact]
    public void Identity_change_on_a_multi_version_container_requires_the_flag()
    {
        using var fx = new RepoFixture();
        var container = SeedNexus(fx, "1.0");
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);

        // The typed guard: catchable specifically by programmatic callers
        // that want to recover onto a confirm flow (it derives from
        // InvalidOperationException, so coarse catches keep working).
        var ex = Assert.Throws<RemovalConfirmationRequiredException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 9 }, "9.1",
            removeOlderVersions: false));
        Assert.Contains("older versions", ex.Message);

        // Nothing was mutated by the refused call.
        var reloaded = fx.Repo.Get(container.Id)!;
        Assert.Equal(2, reloaded.Versions.Count);
    }

    [Fact]
    public void Nexus_to_a_different_id_removes_older_versions_and_resets_remote_claims()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        var older = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var latest = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);
        var olderFolder = older.Versions.Single(v => v.VersionString == "1.0").Folder;
        var latestVersion = latest.Versions.Single(v => v.VersionString == "2.0");
        var latestPath = fx.Repo.GetVersionFolderPath(container.Id, latestVersion.Folder);
        Assert.True(File.Exists(Path.Combine(latestPath, "marker.txt")));

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 9 }, "9.1",
            removeOlderVersions: true);

        // Only the latest survives, keeping its folder + import stamp,
        // carrying the new tag + no remote claims (they belonged to the old
        // identity).
        Assert.NotNull(updated);
        Assert.Equal(9, Assert.IsType<NexusSource>(updated!.Source).ModId);
        var survivor = Assert.Single(updated.Versions);
        Assert.Equal(latestVersion.Folder, survivor.Folder);
        Assert.Equal(latestVersion.ImportedAt, survivor.ImportedAt);
        Assert.Equal("9.1", survivor.VersionString);
        Assert.Null(survivor.FileId);
        Assert.Null(survivor.RemoteUploadedAt);
        Assert.True(survivor.IsLatest);

        // The older version is gone from the manifest + the disk; the
        // survivor's files are untouched.
        Assert.False(Directory.Exists(fx.Repo.GetVersionFolderPath(container.Id, olderFolder)));
        Assert.True(File.Exists(Path.Combine(latestPath, "marker.txt")));
        Assert.Equal(updated.Id, fx.Repo.FindBySource(new NexusSource { ModId = 9 })!.Id);
    }

    [Fact]
    public void Nexus_to_Untracked_clears_the_tag_on_a_single_version()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        var stamped = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT", new UntrackedSource(), "",
            removeOlderVersions: false);

        Assert.NotNull(updated);
        Assert.IsType<UntrackedSource>(updated!.Source);
        var version = Assert.Single(updated.Versions);
        Assert.Equal(string.Empty, version.VersionString);
        // Local facts stay: same folder + import stamp; an ungrounded manual
        // association never carried remote claims to clear.
        Assert.Null(version.FileId);
        Assert.Null(version.RemoteUploadedAt);
        Assert.Equal(stamped.Versions[0].Folder, version.Folder);
        Assert.Equal(stamped.Versions[0].ImportedAt, version.ImportedAt);
        // The untracked-name index now resolves the container by its name.
        Assert.Equal(container.Id, fx.Repo.FindUntrackedByName("WT")!.Id);
    }

    [Fact]
    public void Nexus_to_Untracked_on_a_multi_version_container_removes_older_versions()
    {
        using var fx = new RepoFixture();
        var container = SeedNexus(fx, "1.0");
        var head = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);
        var headFolder = head.Versions.Single(v => v.VersionString == "2.0").Folder;

        Assert.Throws<RemovalConfirmationRequiredException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT", new UntrackedSource(), "", removeOlderVersions: false));

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT", new UntrackedSource(), "", removeOlderVersions: true);

        Assert.NotNull(updated);
        var survivor = Assert.Single(updated!.Versions);
        Assert.Equal(headFolder, survivor.Folder);
        Assert.Equal(string.Empty, survivor.VersionString);
    }

    // ---- persistence ------------------------------------------------------------

    [Fact]
    public void The_edit_persists_to_the_manifest()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "WT");
        fx.Repo.AddVersion(container.Id, "", EmptyPopulate);

        // An untracked rename persists...
        fx.Repo.EditImportDetails(
            container.Id, "WT Renamed", new UntrackedSource(), "",
            removeOlderVersions: false);
        // ...and the follow-on Nexus association (same name: a Nexus
        // destination cannot change it) persists the source + tag.
        fx.Repo.EditImportDetails(
            container.Id, "WT Renamed", new NexusSource { ModId = 55 }, "3.1",
            removeOlderVersions: false);

        var reloaded = fx.Reload().Get(container.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("WT Renamed", reloaded!.Name);
        Assert.Equal(55, Assert.IsType<NexusSource>(reloaded.Source).ModId);
        Assert.Equal("3.1", Assert.Single(reloaded.Versions).VersionString);
    }

    // ---- fixture + helpers --------------------------------------------------------

    private static readonly Action<string> EmptyPopulate = dir =>
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
    };

    /// <summary>
    /// Seeds a Nexus mod 8 container whose single version is
    /// <paramref name="version"/> with no download evidence (a manual
    /// association: the only editable Nexus shape). Follow-on AddVersion calls
    /// without remote facts order by import time, so a later call becomes the
    /// container's latest under the effective-timestamp key.
    /// </summary>
    private static ModContainer SeedNexus(RepoFixture fx, string version)
    {
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        fx.Repo.AddVersion(container.Id, version, EmptyPopulate);
        return container;
    }

    private sealed class RepoFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "curator-repo-" + Guid.NewGuid());
        public IModRepository Repo { get; }

        public RepoFixture()
        {
            var config = CuratorConfig.CreateDefault();
            config.ModsFolder = Folder;
            var loader = new FakeConfigLoader { Config = config };
            _provider = new ServiceCollection()
                .AddSingleton<IConfigLoader>(loader)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
            Repo = _provider.GetRequiredService<IModRepository>();
        }

        public IModRepository Reload()
        {
            var config = CuratorConfig.CreateDefault();
            config.ModsFolder = Folder;
            var provider = new ServiceCollection()
                .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config })
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
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
