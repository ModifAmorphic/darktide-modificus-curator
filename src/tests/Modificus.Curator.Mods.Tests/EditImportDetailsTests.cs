using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Mods.Tests;

/// <summary>
/// <see cref="IModRepository.EditImportDetails"/>: every edit branch (rename,
/// same-identity retag, Untracked to Nexus, the Nexus-unknown retag, the
/// identity reset with older-version removal, Nexus to Untracked), the FileId
/// lock, the removeOlderVersions guard, the tag-collision refusal, the
/// duplicate-identity guard, and the untracked-name index coherence across an
/// Untracked rename + a Nexus-to-Untracked swap. Resolves via DI (black-box)
/// against a temp <c>ModsFolder</c>, the established repository test style.
/// </summary>
public sealed class EditImportDetailsTests
{
    private static readonly DateTimeOffset OldStamp =
        new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset NewStamp =
        new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

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
    public void Rename_only_keeps_source_and_all_versions()
    {
        using var fx = new RepoFixture();
        var container = SeedNexus(fx, "1.0");
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, NewStamp);

        var updated = fx.Repo.EditImportDetails(
            container.Id, "Renamed", new NexusSource { ModId = 8 }, "2.0",
            removeOlderVersions: false);

        Assert.NotNull(updated);
        Assert.Equal("Renamed", updated!.Name);
        Assert.Equal(8, Assert.IsType<NexusSource>(updated.Source).ModId);
        Assert.Equal(2, updated.Versions.Count);
        Assert.Contains(updated.Versions, v => v.VersionString == "1.0");
        Assert.Contains(updated.Versions, v => v.VersionString == "2.0");
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
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, NewStamp);

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
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, NewStamp);

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
        var stamped = fx.Repo.AddVersion(container.Id, "", EmptyPopulate, OldStamp);

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

    // ---- the FileId lock -------------------------------------------------------

    [Fact]
    public void FileId_lock_blocks_a_different_Nexus_id()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, OldStamp, remoteFileId: 9001);

        Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 9 }, "1.0",
            removeOlderVersions: false));
    }

    [Fact]
    public void FileId_lock_blocks_Nexus_to_Untracked()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, OldStamp, remoteFileId: 9001);

        Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT", new UntrackedSource(), "",
            removeOlderVersions: false));
    }

    [Fact]
    public void FileId_on_any_version_blocks_even_when_latest_is_clean()
    {
        // The lock reads EVERY version: an older acquired version grounds the
        // identity just as much as the latest.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, OldStamp, remoteFileId: 9001);
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, NewStamp);

        Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
            container.Id, "WT", new NexusSource { ModId = 9 }, "2.0",
            removeOlderVersions: true));
    }

    [Fact]
    public void FileId_lock_allows_a_same_identity_name_and_tag_edit()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, OldStamp, remoteFileId: 9001);

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT Fixed", new NexusSource { ModId = 8 }, "1.0-fix",
            removeOlderVersions: false);

        Assert.NotNull(updated);
        Assert.Equal("WT Fixed", updated!.Name);
        Assert.Equal("1.0-fix", Assert.Single(updated.Versions).VersionString);
        // Same identity: the download facts survive the edit.
        Assert.Equal(9001, Assert.Single(updated.Versions).FileId);
    }

    // ---- the identity reset ------------------------------------------------------

    [Fact]
    public void Identity_change_on_a_multi_version_container_requires_the_flag()
    {
        using var fx = new RepoFixture();
        var container = SeedNexus(fx, "1.0");
        fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, NewStamp);

        var ex = Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
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
        var older = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, OldStamp);
        var latest = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, NewStamp);
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
    public void Nexus_to_Untracked_clears_the_tag_and_remote_claims_on_a_single_version()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        var stamped = fx.Repo.AddVersion(
            container.Id, "1.0", EmptyPopulate, remoteUploadedAt: OldStamp);

        var updated = fx.Repo.EditImportDetails(
            container.Id, "WT", new UntrackedSource(), "",
            removeOlderVersions: false);

        Assert.NotNull(updated);
        Assert.IsType<UntrackedSource>(updated!.Source);
        var version = Assert.Single(updated.Versions);
        Assert.Equal(string.Empty, version.VersionString);
        Assert.Null(version.FileId);
        Assert.Null(version.RemoteUploadedAt);
        // Local facts stay: same folder + import stamp.
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
        var head = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate, NewStamp);
        var headFolder = head.Versions.Single(v => v.VersionString == "2.0").Folder;

        Assert.Throws<InvalidOperationException>(() => fx.Repo.EditImportDetails(
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
    /// <paramref name="version"/> (no FileId: an inferred identity, the only
    /// kind reachable for an identity change). Follow-on AddVersion calls with
    /// a later remote stamp become the container's latest under the
    /// effective-timestamp key.
    /// </summary>
    private static ModContainer SeedNexus(RepoFixture fx, string version)
    {
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 8 }, "WT");
        fx.Repo.AddVersion(container.Id, version, EmptyPopulate, OldStamp);
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
