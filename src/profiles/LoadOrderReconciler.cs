using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles;

/// <summary>
/// Builds a <see cref="LoadOrderPlan"/> for a parsed load-order file against
/// the active profile and the repository: the IO glue over the pure
/// <see cref="LoadOrderPlanner"/>. Owns the base-name resolution for both
/// sides (per-profile-entry policy resolution for profile mods, the latest
/// version or external folder name for repo candidates) through the shared
/// <see cref="ModBaseNames"/> helper, so matching keys on exactly what
/// staging would link (the <c>GetBaseNameCollision</c> precedent).
/// </summary>
/// <remarks>
/// Entries or containers whose base name cannot be resolved (missing
/// container/version, corrupted version folder, unavailable external folder)
/// are omitted from their side: a file line naming such a mod lands in the
/// plan's unmatched rows rather than silently matching nothing. Read-only:
/// no profile or repository mutation happens here; the caller applies the
/// plan.
/// </remarks>
public interface ILoadOrderReconciler
{
    /// <summary>
    /// Reconciles <paramref name="names"/> against the profile and repository.
    /// </summary>
    /// <param name="profileId">The active profile whose entries are matched
    /// first (a profile match wins over any repo candidate).</param>
    /// <param name="names">The parsed file names, in file order.</param>
    /// <exception cref="KeyNotFoundException">The profile id is unknown.</exception>
    LoadOrderPlan Reconcile(Guid profileId, IReadOnlyList<string> names);
}

/// <summary>
/// Default <see cref="ILoadOrderReconciler"/>: resolves both sides from the
/// live profile + repository, then delegates the matching to
/// <see cref="LoadOrderPlanner"/>. Registered in
/// <c>AddProfiles()</c>; singleton (stateless per call).
/// </summary>
internal sealed class LoadOrderReconciler : ILoadOrderReconciler
{
    private readonly IProfileService _profiles;
    private readonly IModRepository _repo;

    public LoadOrderReconciler(IProfileService profiles, IModRepository repo)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    /// <inheritdoc />
    public LoadOrderPlan Reconcile(Guid profileId, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var entries = _profiles.GetModList(profileId);

        // Profile side: resolve each entry's staging base name per its own
        // policy (what staging would link for it). Unresolvable entries are
        // omitted; a file naming them reports unmatched. The resolved
        // version + source also supply the read-only identity facts the
        // review shows (the Nexus id + the version THIS operation will use,
        // so a pin shows the pinned tag and Latest the resolved latest).
        var profileContainerIds = new HashSet<Guid>();
        var profileMods = new List<LoadOrderProfileMod>();
        foreach (var entry in entries)
        {
            profileContainerIds.Add(entry.ContainerId);
            var container = _repo.Get(entry.ContainerId);
            if (container is null)
            {
                continue;
            }

            var baseName = ResolveBaseName(container, entry.Policy);
            if (baseName is null)
            {
                continue;
            }

            var resolvedVersion = container.ResolveVersion(entry.Policy);
            profileMods.Add(new LoadOrderProfileMod(
                entry.ContainerId,
                baseName,
                container.Name,
                container.Source is NexusSource nexus ? nexus.ModId : null,
                resolvedVersion?.VersionString));
        }

        // Repo side: every container NOT in the profile, keyed by the base
        // name its current latest version would stage (a linked container by
        // its external folder's own name; there is no profile policy to
        // resolve for it). Facts resolve the same way, against the Latest
        // policy the add will apply.
        var candidates = new List<LoadOrderRepoCandidate>();
        foreach (var container in _repo.List())
        {
            if (profileContainerIds.Contains(container.Id))
            {
                continue;
            }

            var baseName = ResolveBaseName(container, ModVersionPolicy.Latest);
            if (baseName is null)
            {
                continue;
            }

            var latestVersion = container.Source is LinkedSource
                ? null
                : container.ResolveVersion(ModVersionPolicy.Latest);
            candidates.Add(new LoadOrderRepoCandidate(
                container.Id,
                baseName,
                container.Source is NexusSource,
                container.Name,
                container.Source is NexusSource repoNexus ? repoNexus.ModId : null,
                latestVersion?.VersionString));
        }

        return LoadOrderPlanner.Build(names, profileMods, candidates);
    }

    /// <summary>
    /// Resolves a container's base folder name: the external folder's own
    /// name for a linked container, else the single base directory inside the
    /// policy-resolved version folder (the shared staging helper).
    /// </summary>
    private string? ResolveBaseName(ModContainer container, ModVersionPolicy policy) =>
        container.Source is LinkedSource linked
            ? ModBaseNames.TryResolveLinkedBaseName(linked)
            : ModBaseNames.TryResolveBaseDir(container, policy, _repo) is { } baseDir
                ? Path.GetFileName(baseDir)
                : null;
}
