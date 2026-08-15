using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Default <see cref="IUpdateStateStore"/>. Backs the domain rules over the raw
/// <see cref="IKnownUpdateState.KnownUpdates"/> persistence, hydrating against
/// the caller's candidates + the repository so stale entries self-heal.
/// Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>No caching.</b> Every read goes back through <see cref="IKnownUpdateState"/>
/// (the store is itself a cached singleton that rewrites the whole model on each
/// write) + through the repository for the live filter. The state file is tiny
/// and these surfaces are cheap, so the honest model is to re-read on every call
/// rather than hold a cache that could drift from a concurrent write.</para>
/// <para>
/// <b>Concurrency.</b> The check completes on a threadpool task; the UI reads on
/// the UI thread. The backing store serializes its own writes under a
/// lock, and the filter reads here are idempotent (a stale entry dropped twice is
/// harmless), so no additional synchronization is needed.</para>
/// </remarks>
internal sealed class UpdateStateStore : IUpdateStateStore
{
    private readonly IKnownUpdateState _appState;
    private readonly IModRepository _repository;
    private readonly ILogger<UpdateStateStore> _logger;

    public UpdateStateStore(
        IKnownUpdateState appState,
        IModRepository repository,
        ILogger<UpdateStateStore> logger)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void RecordResult(Guid profileId, UpdateCheckResult result)
    {
        // Only an authoritative success replaces state. NoNexusMods is a
        // local-truth clear (no applicable Nexus update can exist). Every other
        // outcome preserves prior knowledge.
        if (result.Outcome == CheckOutcome.Success)
        {
            var snapshots = result.Updates
                .Select(u => new KnownUpdateSnapshot(
                    profileId,
                    u.ContainerId,
                    u.ModId,
                    u.CurrentVersion,
                    result.CheckedAt,
                    u.LatestUpdateAt))
                .ToList();
            SetProfileSnapshots(profileId, snapshots);
            _logger.LogInformation(
                "Recorded authoritative update state for profile {Profile}: {Count} flagged.",
                profileId, snapshots.Count);
            return;
        }

        if (result.Outcome == CheckOutcome.NoNexusMods)
        {
            SetProfileSnapshots(profileId, new List<KnownUpdateSnapshot>());
            _logger.LogInformation(
                "Cleared update state for profile {Profile}: no Nexus mods.", profileId);
            return;
        }

        // NoAuth, RateLimited, Failed: preserve prior state.
    }

    /// <inheritdoc />
    public void AcknowledgeInstall(Guid profileId, Guid containerId)
    {
        var current = GetProfileSnapshots(profileId);
        if (current.Count == 0)
        {
            return;
        }

        var filtered = current.Where(s => s.ContainerId != containerId).ToList();
        if (filtered.Count == current.Count)
        {
            // Nothing matched; avoid a redundant write.
            return;
        }

        SetProfileSnapshots(profileId, filtered);
        _logger.LogInformation(
            "Acknowledged install: cleared known update for container {Container} in profile {Profile}.",
            containerId, profileId);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> GetKnownUpdateContainerIds(
        Guid profileId, IReadOnlyList<ModListCandidate> candidates)
    {
        var current = GetProfileSnapshots(profileId);
        if (current.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var valid = FilterValid(candidates, current);

        // Self-heal: persist the filtered set so the next read skips the stale
        // ones without re-filtering. A no-change filter is a no-op write (the
        // store rewrites the whole model, but the value is unchanged).
        if (valid.Count != current.Count)
        {
            SetProfileSnapshots(profileId, valid);
        }

        return valid.Select(s => s.ContainerId).ToHashSet();
    }

    /// <summary>
    /// Drops entries whose membership, policy, source, or installed version no
    /// longer match the caller's candidates + the repository state. A kept entry
    /// must:
    /// <list type="bullet">
    /// <item>still be a member of the candidate list (not removed),</item>
    /// <item>still be on <see cref="LatestPolicy"/> (not re-pinned),</item>
    /// <item>still resolve to a <see cref="NexusSource"/> container with the
    /// same <see cref="NexusSource.ModId"/> (not source-changed), and</item>
    /// <item>still have the same installed <see cref="KnownUpdateSnapshot.CurrentVersion"/>
    /// (not locally version-changed).</item>
    /// </list>
    /// </summary>
    private List<KnownUpdateSnapshot> FilterValid(
        IReadOnlyList<ModListCandidate> candidates, List<KnownUpdateSnapshot> snapshots)
    {
        // Index the candidates by container id once for an O(1) lookup. A
        // missing entry means the mod was removed from the profile.
        var candidatesById = new Dictionary<Guid, ModListCandidate>();
        foreach (var candidate in candidates)
        {
            candidatesById.TryAdd(candidate.ContainerId, candidate);
        }

        var valid = new List<KnownUpdateSnapshot>();
        foreach (var snapshot in snapshots)
        {
            if (!candidatesById.TryGetValue(snapshot.ContainerId, out var candidate))
            {
                continue; // removed from the profile
            }

            if (candidate.Policy is not LatestPolicy)
            {
                continue; // re-pinned
            }

            var container = _repository.Get(snapshot.ContainerId);
            if (container is null)
            {
                continue; // container gone from the repository
            }

            if (container.Source is not NexusSource nexus || nexus.ModId != snapshot.ModId)
            {
                continue; // source changed / no longer the same Nexus mod
            }

            var installedVersion = container.ResolveVersion(new LatestPolicy())?.VersionString
                ?? string.Empty;
            if (!string.Equals(installedVersion, snapshot.CurrentVersion,
                StringComparison.OrdinalIgnoreCase))
            {
                continue; // local version changed since the flag was recorded
            }

            valid.Add(snapshot);
        }

        return valid;
    }

    /// <summary>
    /// Reads the persisted snapshots for <paramref name="profileId"/> as a
    /// mutable list (empty when none are recorded for the profile).
    /// </summary>
    private List<KnownUpdateSnapshot> GetProfileSnapshots(Guid profileId)
    {
        var map = _appState.KnownUpdates;
        if (map is null || !map.TryGetValue(profileId, out var list) || list is null)
        {
            return new List<KnownUpdateSnapshot>();
        }
        return new List<KnownUpdateSnapshot>(list);
    }

    /// <summary>
    /// Replaces <paramref name="profileId"/>'s persisted snapshots with
    /// <paramref name="snapshots"/> (a whole-model rewrite via the store), clearing
    /// the profile's entry when the list is empty so the persisted map does not
    /// accumulate empty lists for every profile ever checked.
    /// </summary>
    private void SetProfileSnapshots(Guid profileId, List<KnownUpdateSnapshot> snapshots)
    {
        var map = _appState.KnownUpdates is { } existing
            ? new Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>(existing)
            : new Dictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>();

        if (snapshots.Count == 0)
        {
            map.Remove(profileId);
        }
        else
        {
            map[profileId] = snapshots;
        }

        _appState.KnownUpdates = map.Count == 0 ? null : map;
    }
}
