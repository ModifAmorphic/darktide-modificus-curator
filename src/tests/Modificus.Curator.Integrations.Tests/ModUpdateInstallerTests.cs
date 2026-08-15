using Modificus.Curator.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// <see cref="ModUpdateInstaller"/> unit tests: the gate semantics (TryInstall
/// refuses politely when held, InstallLatest awaits its turn), the in-gate
/// eligibility revalidation, acknowledge-on-success-only, the per-attempt
/// progress bracket, the outcome record shape, and cancellation propagation.
/// These are the contracts both the manual per-row action and the automatic
/// Premium batch rely on (their UI-level glue is covered by the UI tests).
/// </summary>
public sealed class ModUpdateInstallerTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();

    private readonly FakeAcquisition _acquisition = new();
    private readonly FakeStateStore _state = new();
    private readonly FakeRepository _repository = new();
    private readonly UpdateCoordinator _coordinator = new();
    private readonly List<ModUpdateProgressEventArgs> _progress = new();

    private ModUpdateInstaller BuildInstaller()
    {
        var installer = new ModUpdateInstaller(
            _acquisition, _state, _repository, _coordinator,
            NullLogger<ModUpdateInstaller>.Instance);
        installer.ModUpdateProgress += (_, e) => _progress.Add(e);
        return installer;
    }

    private static ModListCandidate Candidate(Guid containerId, ModVersionPolicy? policy = null) =>
        new(containerId, policy ?? new LatestPolicy());

    private ModContainer SeedNexusContainer(Guid id, int modId, string version)
    {
        var container = new ModContainer
        {
            Id = id,
            Source = new NexusSource { ModId = modId },
            Name = "Mod " + modId,
            Versions = new[]
            {
                new ModVersion
                {
                    Folder = id.ToString("N") + "-v",
                    VersionString = version,
                    IsLatest = true,
                    ImportedAt = DateTimeOffset.UtcNow,
                },
            },
        };
        _repository.Containers[id] = container;
        return container;
    }

    // ---- success path ------------------------------------------------------

    [Fact]
    public async Task TryInstall_acquires_the_latest_release_and_acknowledges_once()
    {
        var installer = BuildInstaller();
        var container = SeedNexusContainer(Guid.NewGuid(), 8, "1.0");

        var outcome = await installer.TryInstallLatestAsync(
            ProfileId, container.Id, 8, "1.0", new[] { Candidate(container.Id) });

        Assert.Equal(ModInstallStatus.Installed, outcome.Status);
        // The acquisition targeted the Darktide domain + the mod id.
        var call = Assert.Single(_acquisition.LatestCalls);
        Assert.Equal(("warhammer40kdarktide", 8), call);
        // Acknowledged exactly once, only now.
        Assert.Equal((ProfileId, container.Id), Assert.Single(_state.AcknowledgeCalls));
        // Progress bracketed the attempt.
        Assert.Equal(2, _progress.Count);
        Assert.Equal(container.Id, _progress[0].ContainerId);
        Assert.True(_progress[0].IsActive);
        Assert.Equal(container.Id, _progress[1].ContainerId);
        Assert.False(_progress[1].IsActive);
        // The gate is not stuck.
        Assert.False(installer.IsBusy);
    }

    [Fact]
    public async Task InstallLatest_behaves_identically_on_the_success_path()
    {
        var installer = BuildInstaller();
        var container = SeedNexusContainer(Guid.NewGuid(), 8, "1.0");

        var outcome = await installer.InstallLatestAsync(
            ProfileId, container.Id, 8, "1.0", new[] { Candidate(container.Id) });

        Assert.Equal(ModInstallStatus.Installed, outcome.Status);
        Assert.Single(_acquisition.LatestCalls);
        Assert.Single(_state.AcknowledgeCalls);
        Assert.Equal(2, _progress.Count);
    }

    // ---- the gate ----------------------------------------------------------

    [Fact]
    public async Task TryInstall_returns_Busy_when_the_gate_is_held_and_touches_nothing()
    {
        // A held gate models EITHER a manual install or an automatic batch
        // entry in flight: the coordinator is the single mutual-exclusion point
        // across both paths.
        var installer = BuildInstaller();
        var container = SeedNexusContainer(Guid.NewGuid(), 8, "1.0");
        Assert.True(_coordinator.TryAcquire(out var held));

        var outcome = await installer.TryInstallLatestAsync(
            ProfileId, container.Id, 8, "1.0", new[] { Candidate(container.Id) });

        Assert.Equal(ModInstallStatus.Busy, outcome.Status);
        Assert.Equal(string.Empty, outcome.Reason);
        Assert.Null(outcome.Exception);
        Assert.Empty(_acquisition.LatestCalls);
        Assert.Empty(_state.AcknowledgeCalls);
        Assert.Empty(_progress);
        held?.Dispose();
    }

    [Fact]
    public async Task InstallLatest_waits_its_turn_behind_a_held_gate()
    {
        // The automatic semantics: rather than refusing, the await blocks on
        // the shared gate. The cancellation token ends the wait here (a held
        // gate would otherwise block the test forever).
        var installer = BuildInstaller();
        var container = SeedNexusContainer(Guid.NewGuid(), 8, "1.0");
        Assert.True(_coordinator.TryAcquire(out var held));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            installer.InstallLatestAsync(
                ProfileId, container.Id, 8, "1.0", new[] { Candidate(container.Id) }, cts.Token));

        Assert.Empty(_acquisition.LatestCalls);
        Assert.Empty(_state.AcknowledgeCalls);
        held?.Dispose();
        Assert.False(installer.IsBusy);
    }

    [Fact]
    public void IsBusy_and_BusyChanged_mirror_the_coordinator()
    {
        var installer = BuildInstaller();
        var raised = 0;
        installer.BusyChanged += (_, _) => raised++;

        Assert.False(installer.IsBusy);
        Assert.True(_coordinator.TryAcquire(out var scope));
        Assert.True(installer.IsBusy);
        Assert.Equal(1, raised);

        scope?.Dispose();
        Assert.False(installer.IsBusy);
        Assert.Equal(2, raised);
    }

    // ---- in-gate eligibility ----------------------------------------------

    [Fact]
    public async Task Removed_candidate_is_NotEligible_and_installs_nothing()
    {
        var installer = BuildInstaller();
        var container = SeedNexusContainer(Guid.NewGuid(), 8, "1.0");

        // No candidate for the container: the mod left the profile.
        var outcome = await installer.TryInstallLatestAsync(
            ProfileId, container.Id, 8, "1.0", Array.Empty<ModListCandidate>());

        Assert.Equal(ModInstallStatus.NotEligible, outcome.Status);
        Assert.Equal("removed from profile", outcome.Reason);
        Assert.Null(outcome.Exception);
        Assert.Empty(_acquisition.LatestCalls);
        Assert.Empty(_state.AcknowledgeCalls);
        // The progress bracket still fired (the attempt started + stopped).
        Assert.Equal(2, _progress.Count);
    }

    [Fact]
    public async Task Version_changed_candidate_is_NotEligible()
    {
        var installer = BuildInstaller();
        var container = SeedNexusContainer(Guid.NewGuid(), 8, "2.0");

        // The flag was recorded against "1.0"; the installed version moved on
        // (already updated out of band): a stale flag must not reinstall.
        var outcome = await installer.TryInstallLatestAsync(
            ProfileId, container.Id, 8, "1.0", new[] { Candidate(container.Id) });

        Assert.Equal(ModInstallStatus.NotEligible, outcome.Status);
        Assert.Equal("version changed", outcome.Reason);
        Assert.Empty(_acquisition.LatestCalls);
    }

    [Fact]
    public async Task Re_pinned_candidate_is_NotEligible()
    {
        var installer = BuildInstaller();
        var container = SeedNexusContainer(Guid.NewGuid(), 8, "1.0");

        var outcome = await installer.TryInstallLatestAsync(
            ProfileId, container.Id, 8, "1.0",
            new[] { Candidate(container.Id, new PinnedPolicy("v")) });

        Assert.Equal(ModInstallStatus.NotEligible, outcome.Status);
        Assert.Equal("re-pinned", outcome.Reason);
    }

    // ---- failure + cancellation -------------------------------------------

    [Fact]
    public async Task Acquisition_failure_returns_Failed_with_the_exception_and_never_acknowledges()
    {
        var installer = BuildInstaller();
        var container = SeedNexusContainer(Guid.NewGuid(), 8, "1.0");
        var boom = new InvalidOperationException("boom");
        _acquisition.ThrowNext = boom;

        var outcome = await installer.TryInstallLatestAsync(
            ProfileId, container.Id, 8, "1.0", new[] { Candidate(container.Id) });

        Assert.Equal(ModInstallStatus.Failed, outcome.Status);
        Assert.Same(boom, outcome.Exception);
        Assert.Equal("boom", outcome.Reason);
        // Acknowledge happens ONLY on success, exactly once.
        Assert.Empty(_state.AcknowledgeCalls);
        // The spinner bracket still closed.
        Assert.Equal(2, _progress.Count);
        Assert.False(_progress[1].IsActive);
        Assert.False(installer.IsBusy);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_becoming_an_outcome()
    {
        var installer = BuildInstaller();
        var container = SeedNexusContainer(Guid.NewGuid(), 8, "1.0");
        _acquisition.ThrowNext = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            installer.TryInstallLatestAsync(
                ProfileId, container.Id, 8, "1.0", new[] { Candidate(container.Id) }));

        // Not a failure outcome, no acknowledge, spinner closed.
        Assert.Empty(_state.AcknowledgeCalls);
        Assert.Equal(2, _progress.Count);
        Assert.False(_progress[1].IsActive);
        Assert.False(installer.IsBusy);
    }

    // ---- fakes -------------------------------------------------------------

    /// <summary>
    /// A configurable <see cref="IModAcquisitionService"/> stub: records
    /// AcquireLatestNexusAsync calls (game domain + mod id) + optionally throws.
    /// </summary>
    private sealed class FakeAcquisition : IModAcquisitionService
    {
        public List<(string GameDomain, int ModId)> LatestCalls { get; } = new();
        public Exception? ThrowNext { get; set; }

        public Task<(Guid ContainerId, string VersionId)> AcquireFromNexusAsync(
            string gameDomain, int modId, int fileId,
            string? nxmKey = null, long? nxmExpires = null,
            IProgress<long>? progress = null, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<(Guid ContainerId, string VersionId)> AcquireLatestNexusAsync(
            string gameDomain, int modId,
            IProgress<long>? progress = null, CancellationToken ct = default)
        {
            LatestCalls.Add((gameDomain, modId));
            if (ThrowNext is not null)
            {
                return Task.FromException<(Guid, string)>(ThrowNext);
            }
            return Task.FromResult((Guid.NewGuid(), "v"));
        }
    }

    /// <summary>
    /// A recording <see cref="IUpdateStateStore"/> stub: only the acknowledge
    /// surface is exercised by the installer.
    /// </summary>
    private sealed class FakeStateStore : IUpdateStateStore
    {
        public List<(Guid ProfileId, Guid ContainerId)> AcknowledgeCalls { get; } = new();

        public void RecordResult(Guid profileId, UpdateCheckResult result) { }

        public void AcknowledgeInstall(Guid profileId, Guid containerId) =>
            AcknowledgeCalls.Add((profileId, containerId));

        public IReadOnlyCollection<Guid> GetKnownUpdateContainerIds(
            Guid profileId, IReadOnlyList<ModListCandidate> candidates) =>
            Array.Empty<Guid>();
    }

    /// <summary>
    /// An in-memory <see cref="IModRepository"/> stub: only <see cref="Get"/>
    /// is exercised by the installer.
    /// </summary>
    private sealed class FakeRepository : IModRepository
    {
        public Dictionary<Guid, ModContainer> Containers { get; } = new();

        public ModContainer? Get(Guid containerId) =>
            Containers.TryGetValue(containerId, out var c) ? c : null;

        public ModContainer? RenameContainer(Guid containerId, string newName) =>
            throw new NotImplementedException();

        public IReadOnlyList<ModContainer> List() => throw new NotImplementedException();
        public ModContainer? FindBySource(ModSource source) => throw new NotImplementedException();
        public ModContainer? FindUntrackedByName(string name) => throw new NotImplementedException();
        public ModContainer CreateContainer(ModSource source, string name) => throw new NotImplementedException();
        public ModContainer AddVersion(
            Guid containerId, string versionString, Action<string> populateFolder,
            DateTimeOffset? remoteUploadedAt = null, ModDisplayMetadata? displayMetadata = null)
            => throw new NotImplementedException();
        public bool TryInitializeDisplayMetadata(Guid containerId, ModDisplayMetadata metadata)
            => throw new NotImplementedException();
        public void RemoveVersion(Guid containerId, string versionFolder) => throw new NotImplementedException();
        public string GetVersionFolderPath(Guid containerId, string versionFolder) => throw new NotImplementedException();
        public void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced) => throw new NotImplementedException();
        public bool IsExternalAvailable(Guid containerId) => throw new NotImplementedException();
    }
}
