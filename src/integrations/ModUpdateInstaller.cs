using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Default <see cref="IModUpdateInstaller"/>. Registered as a singleton. See
/// the interface remarks for the gating, revalidation, acknowledge, progress,
/// and error contracts.
/// </summary>
/// <remarks>
/// Library code with no UI-thread affinity: callers invoke it from whatever
/// context they hold (the manual path from the UI thread, the automatic batch
/// from the runner's captured UI context), and the acquisition's own I/O runs
/// on the threadpool internally. Awaiting without
/// <c>ConfigureAwait(false)</c> keeps each caller's context, which both
/// callers rely on.
/// </remarks>
internal sealed class ModUpdateInstaller : IModUpdateInstaller
{
    private readonly IModAcquisitionService _acquisition;
    private readonly IUpdateStateStore _updateState;
    private readonly IModRepository _repository;
    private readonly UpdateCoordinator _coordinator;
    private readonly ILogger<ModUpdateInstaller> _logger;

    public ModUpdateInstaller(
        IModAcquisitionService acquisition,
        IUpdateStateStore updateState,
        IModRepository repository,
        UpdateCoordinator coordinator,
        ILogger<ModUpdateInstaller> logger)
    {
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsBusy => _coordinator.IsBusy;

    /// <inheritdoc />
    public event EventHandler? BusyChanged
    {
        add => _coordinator.BusyChanged += value;
        remove => _coordinator.BusyChanged -= value;
    }

    /// <inheritdoc />
    public event EventHandler<ModUpdateProgressEventArgs>? ModUpdateProgress;

    /// <inheritdoc />
    public async Task<ModInstallOutcome> TryInstallLatestAsync(
        Guid profileId,
        Guid containerId,
        int modId,
        string expectedVersion,
        IReadOnlyList<ModListCandidate> candidates,
        CancellationToken ct = default)
    {
        if (!_coordinator.TryAcquire(out var scope))
        {
            return new ModInstallOutcome(ModInstallStatus.Busy);
        }

        using var _ = scope;
        return await InstallCoreAsync(profileId, containerId, modId, expectedVersion, candidates, ct);
    }

    /// <inheritdoc />
    public async Task<ModInstallOutcome> InstallLatestAsync(
        Guid profileId,
        Guid containerId,
        int modId,
        string expectedVersion,
        IReadOnlyList<ModListCandidate> candidates,
        CancellationToken ct = default)
    {
        using var scope = await _coordinator.AcquireAsync(ct);
        return await InstallCoreAsync(profileId, containerId, modId, expectedVersion, candidates, ct);
    }

    /// <summary>
    /// The shared install body, run under the acquired gate: revalidate
    /// eligibility, acquire the latest Nexus release, acknowledge on success.
    /// Progress start/stop brackets the whole attempt in a finally so no
    /// outcome leaves a subscriber's spinner stuck.
    /// </summary>
    private async Task<ModInstallOutcome> InstallCoreAsync(
        Guid profileId,
        Guid containerId,
        int modId,
        string expectedVersion,
        IReadOnlyList<ModListCandidate> candidates,
        CancellationToken ct)
    {
        var candidate = candidates.FirstOrDefault(c => c.ContainerId == containerId);
        RaiseProgress(containerId, active: true);
        try
        {
            if (!UpdateEligibility.IsEligible(
                    candidate, _repository.Get(containerId), modId, expectedVersion, out var reason))
            {
                _logger.LogDebug(
                    "Mod update for container {Container} skipped: {Reason}.",
                    containerId, reason);
                return new ModInstallOutcome(ModInstallStatus.NotEligible, reason);
            }

            await _acquisition.AcquireLatestNexusAsync(NexusGameIdentity.DarktideDomain, modId, ct: ct);

            // Acknowledge exactly once, only on success: the persisted entry
            // that produced the flag is cleared without an extra API check, so
            // the next hydration no longer flags the container.
            _updateState.AcknowledgeInstall(profileId, containerId);
            _logger.LogInformation(
                "Installed the latest Nexus release for container {Container} (mod {Mod}).",
                containerId, modId);
            return new ModInstallOutcome(ModInstallStatus.Installed);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a failure outcome: it propagates so each
            // caller keeps its own cancellation posture.
            _logger.LogInformation("Mod update for container {Container} was cancelled.", containerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Mod update for container {Container} (mod {Mod}) failed.",
                containerId, modId);
            return new ModInstallOutcome(ModInstallStatus.Failed, ex.Message, ex);
        }
        finally
        {
            RaiseProgress(containerId, active: false);
        }
    }

    /// <summary>
    /// Raises <see cref="ModUpdateProgress"/> for <paramref name="containerId"/>.
    /// Called immediately after the gate is acquired (active=true) and from the
    /// attempt's finally block (active=false). Subscribers marshal to the UI
    /// thread; this method fires on the caller's thread.
    /// </summary>
    private void RaiseProgress(Guid containerId, bool active) =>
        ModUpdateProgress?.Invoke(this, new ModUpdateProgressEventArgs(containerId, active));
}
