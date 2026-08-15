using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Default <see cref="INexusModMetadataService"/>. Backfills missing display
/// metadata through the stable Nexus v1 <c>mods/{id}.json</c> endpoint, one call
/// per candidate, at most 25 attempted calls per pass and one real pass per
/// persisted 24-hour window. Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>No extra API surface.</b> The pass reuses the same
/// <see cref="INexusClient.GetModInfoAsync"/> the acquisition path already
/// calls, and the same <c>ModDisplayMetadataMapper</c> normalization, so the
/// wire semantics cannot drift between acquisition and backfill.</para>
/// <para>
/// <b>Serialized passes.</b> A <see cref="SemaphoreSlim"/> serializes
/// overlapping invocations. After acquiring it, the service rechecks the
/// persisted 24-hour gate so a second call that arrived while the first was
/// running returns empty rather than starting a second pass inside the window.
/// </para>
/// <para>
/// <b>Never throws (except cancellation).</b> Config load, app-state read, gate
/// math, candidate enumeration, the API call, mapping, and persistence are all
/// inside the never-throws boundary. <see cref="OperationCanceledException"/>
/// propagates; every other exception is logged and absorbed into an empty or
/// partial result.</para>
/// </remarks>
internal sealed class NexusModMetadataService : INexusModMetadataService
{
    private const int MaxAttempts = 25;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly INexusClient _nexus;
    private readonly IModRepository _repository;
    private readonly IConfigLoader _configLoader;
    private readonly IAppStateStore _appState;
    private readonly ILogger<NexusModMetadataService> _logger;
    private readonly Func<DateTimeOffset> _getNow;

    private readonly SemaphoreSlim _passLock = new(1, 1);

    public NexusModMetadataService(
        INexusClient nexus,
        IModRepository repository,
        IConfigLoader configLoader,
        IAppStateStore appState,
        ILogger<NexusModMetadataService> logger,
        Func<DateTimeOffset>? getNow = null)
    {
        _nexus = nexus ?? throw new ArgumentNullException(nameof(nexus));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _getNow = getNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<NexusModMetadataResult> BackfillMissingAsync(
        IReadOnlyList<Guid> priorityContainerIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(priorityContainerIds);

        var updated = new Dictionary<Guid, ModDisplayMetadata>();
        var attempted = 0;
        var rateLimited = false;
        DateTimeOffset? rateLimitResetsAt = null;

        await _passLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Auth gate.
            var authMethod = _configLoader.Load().Integrations.Nexus.AuthMethod;
            if (authMethod == NexusAuthMethod.None)
            {
                _logger.LogDebug("Metadata backfill skipped: Nexus auth not configured.");
                return NexusModMetadataResult.Empty;
            }

            // 2. Persisted 24-hour gate (rechecked after acquiring the lock).
            var now = _getNow();
            var lastStamp = _appState.LastNexusMetadataBackfillUtc;
            if (lastStamp.HasValue && IsWithinGate(lastStamp.Value, now))
            {
                _logger.LogDebug("Metadata backfill skipped: within the 24-hour gate.");
                return NexusModMetadataResult.Empty;
            }

            // 3. Candidate sequence.
            var candidates = BuildCandidates(priorityContainerIds);
            if (candidates.Count == 0)
            {
                _logger.LogDebug("Metadata backfill skipped: no candidates missing metadata.");
                return NexusModMetadataResult.Empty;
            }

            // 4. Per-candidate loop.
            foreach (var (containerId, modId) in candidates)
            {
                // Pre-request recheck (optimization; the correctness boundary
                // is the atomic TryInitialize below).
                var before = _repository.Get(containerId);
                if (before is null || before.DisplayMetadata is not null)
                {
                    continue;
                }

                if (attempted >= MaxAttempts)
                {
                    _logger.LogInformation(
                        "Metadata backfill reached the {Max} attempted-container cap.",
                        MaxAttempts);
                    break;
                }

                // Attempt the API call.
                Response<ModInfo> response;
                attempted++;
                try
                {
                    response = await _nexus.GetModInfoAsync(NexusGameIdentity.DarktideDomain, modId, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (NexusRateLimitException ex)
                {
                    rateLimited = true;
                    rateLimitResetsAt = NexusRateLimitReset.ComputeEarliest(ex.Limits, _getNow());
                    _logger.LogInformation(ex,
                        "Metadata backfill rate-limited at mod {ModId}; stopping pass.", modId);
                    break;
                }
                catch (NexusNotAuthenticatedException ex)
                {
                    _logger.LogInformation(ex,
                        "Metadata backfill stopped: Nexus auth no longer configured.");
                    break;
                }
                catch (NexusApiException ex)
                {
                    _logger.LogWarning(ex,
                        "Metadata backfill: mod {ModId} failed; continuing.", modId);
                    continue;
                }
                // Any other exception (transport, unexpected) propagates to the
                // outer catch, which absorbs it into the partial result.

                // 5. Map + persist.
                if (response.Data is not null)
                {
                    ModDisplayMetadata metadata;
                    try
                    {
                        metadata = ModDisplayMetadataMapper.ToDisplayMetadata(response.Data);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Metadata backfill: mapping mod {ModId} failed; skipping.", modId);
                        if (CheckRateLimitAfterResponse(response, ref rateLimited,
                            ref rateLimitResetsAt, modId))
                        {
                            break;
                        }
                        continue;
                    }

                    // Atomic missing-only initialization. The correctness
                    // boundary: if a concurrent writer set metadata during the
                    // await, this returns false and the container is not
                    // reported as Updated.
                    if (_repository.TryInitializeDisplayMetadata(containerId, metadata))
                    {
                        updated[containerId] = metadata;
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Metadata backfill: container {Id} already has metadata; not overwritten.",
                            containerId);
                    }
                }

                // 6. Exhausted-counter check.
                if (CheckRateLimitAfterResponse(response, ref rateLimited,
                    ref rateLimitResetsAt, modId))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The never-throws boundary for the whole pass: any non-cancellation
            // failure (config load, app-state read, candidate enumeration, API
            // transport, mapping, repository write, result construction) is
            // logged + absorbed into the partial state accumulated so far.
            _logger.LogWarning(ex,
                "Metadata backfill pass stopped after an unexpected failure ({Attempted} attempted).",
                attempted);
        }
        finally
        {
            // Stamp only when at least one API request was attempted.
            StampIfAttempted(attempted);
            _passLock.Release();
        }

        return new NexusModMetadataResult(updated, attempted, rateLimited, rateLimitResetsAt);
    }

    private void StampIfAttempted(int attempted)
    {
        if (attempted <= 0)
        {
            return;
        }
        try
        {
            _appState.LastNexusMetadataBackfillUtc = _getNow();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metadata backfill: failed to persist the gate timestamp.");
        }
    }

    /// <summary>
    /// Checks whether the successful response headers report an exhausted rate-
    /// limit window; if so, sets the rate-limit fields and returns true.
    /// </summary>
    private bool CheckRateLimitAfterResponse(
        Response<ModInfo> response,
        ref bool rateLimited,
        ref DateTimeOffset? rateLimitResetsAt,
        int modId)
    {
        var limits = response.RateLimits;
        if (!IsReportedExhausted(limits))
        {
            return false;
        }
        rateLimited = true;
        rateLimitResetsAt = NexusRateLimitReset.ComputeEarliest(limits, _getNow());
        _logger.LogInformation(
            "Metadata backfill rate-limited after mod {ModId} (daily {DR}/{DL}, hourly {HR}/{HL}); stopping pass.",
            modId, limits.DailyRemaining, limits.DailyLimit,
            limits.HourlyRemaining, limits.HourlyLimit);
        return true;
    }

    /// <summary>
    /// Builds the candidate sequence: priority ids first (distinct, in caller
    /// order), then the remaining repository containers in deterministic Guid
    /// order. Each candidate is re-resolved from the repository and included
    /// only when it is a <see cref="NexusSource"/> container whose
    /// <see cref="ModContainer.DisplayMetadata"/> is <c>null</c>.
    /// </summary>
    private List<(Guid ContainerId, int ModId)> BuildCandidates(
        IReadOnlyList<Guid> priorityContainerIds)
    {
        var seen = new HashSet<Guid>();
        var candidates = new List<(Guid ContainerId, int ModId)>();

        foreach (var id in priorityContainerIds)
        {
            if (seen.Add(id))
            {
                AddCandidate(id, candidates);
            }
        }

        foreach (var container in _repository.List().OrderBy(c => c.Id, Comparer<Guid>.Default))
        {
            if (seen.Add(container.Id))
            {
                AddCandidate(container.Id, candidates);
            }
        }

        return candidates;

        void AddCandidate(Guid id, List<(Guid, int)> list)
        {
            var container = _repository.Get(id);
            if (container is null) return;
            if (container.Source is not NexusSource nexus) return;
            if (container.DisplayMetadata is not null) return;
            list.Add((id, nexus.ModId));
        }
    }

    /// <summary>
    /// Whether the elapsed time since <paramref name="lastStamp"/> is less than
    /// the gate interval. Overflow-safe at <see cref="DateTimeOffset"/> extremes:
    /// a future <paramref name="lastStamp"/> (clock skew) gates without any
    /// subtraction; a past stamp subtracts a non-negative duration that is always
    /// within <see cref="TimeSpan"/> range. Exactly the interval elapsed does NOT
    /// gate (strict less-than).
    /// </summary>
    private bool IsWithinGate(DateTimeOffset lastStamp, DateTimeOffset now)
    {
        if (lastStamp > now)
        {
            return true;
        }
        return now - lastStamp < Interval;
    }

    private static bool IsReportedExhausted(NexusRateLimits limits) =>
        (limits.DailyLimit > 0 && limits.DailyRemaining <= 0)
        || (limits.HourlyLimit > 0 && limits.HourlyRemaining <= 0);
}
