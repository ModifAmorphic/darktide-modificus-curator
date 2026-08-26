namespace Modificus.Curator.Profiles;

/// <summary>
/// One parsed file line's reconciliation outcome: the mod is already in the
/// active profile (its position updates), the mod is in the repository but
/// not the profile (a candidate add), or no local match exists.
/// </summary>
public enum LoadOrderLineOutcome
{
    /// <summary>The line matched a profile member; applying moves it to the file's position.</summary>
    Reorder,

    /// <summary>The line matched a repository container that is not in the profile; applying (when included) adds it.</summary>
    LibraryAdd,

    /// <summary>No local match (or an ambiguous one); the line is reported, never silently dropped.</summary>
    Unresolved,

    /// <summary>
    /// The line has no profile or repository match, but the txt's own
    /// directory contains a mod folder with this name (a sibling of the txt,
    /// carrying <c>&lt;name&gt;/&lt;name&gt;.mod</c>): applying (when
    /// included) imports that folder. The planner reports these lines
    /// unmatched; the load-order card upgrades them after scanning the
    /// picked file's directory (the reconciler has no path input, so the
    /// disk fact arrives at the card).
    /// </summary>
    SiblingImport,
}

/// <summary>
/// An active-profile mod, projected for load-order matching: its container id
/// and the base folder name the profile would stage for it (resolved by the
/// caller per the entry's policy, the <c>GetBaseNameCollision</c> precedent;
/// entries with no resolvable base name are omitted by the caller, so a file
/// naming them lands in <c>UnmatchedNames</c> rather than silently matching).
/// </summary>
/// <param name="ContainerId">The profile entry's container.</param>
/// <param name="BaseName">The resolved staging base folder name.</param>
/// <param name="DisplayName">The mod's display name (the row name the review
/// table shows for a match).</param>
public sealed record LoadOrderProfileMod(Guid ContainerId, string BaseName, string DisplayName);

/// <summary>
/// A repository container that is NOT in the active profile, projected for
/// load-order matching: the base folder name its resolved (latest) version
/// would stage, whether it is Nexus-sourced (the ambiguity preference), and
/// its display name.
/// </summary>
/// <param name="ContainerId">The container.</param>
/// <param name="BaseName">The resolved staging base folder name of the
/// container's current latest version (or, for a linked container, the
/// external folder's own name).</param>
/// <param name="IsNexusSourced">Whether the container's source is a
/// <see cref="Mods.NexusSource"/>; used only to break same-base-name
/// ambiguity.</param>
/// <param name="DisplayName">The container's display name (what the review
/// table shows for a match).</param>
public sealed record LoadOrderRepoCandidate(
    Guid ContainerId,
    string BaseName,
    bool IsNexusSourced,
    string DisplayName);

/// <summary>
/// One file line's reconciliation row: the file's folder name, the outcome,
/// and the container it matched (null when unresolved). Rows preserve file
/// order; the table renders them one-to-one.
/// </summary>
/// <param name="Name">The file's (trimmed, deduplicated) folder name.</param>
/// <param name="Outcome">The reconciliation outcome.</param>
/// <param name="ContainerId">The matched container, or null when
/// unresolved.</param>
/// <param name="MatchedBaseName">The base name that matched, or null when
/// unresolved (kept so the review can show what the match keyed on).</param>
/// <param name="DisplayName">The matched mod's display name, or null when
/// unresolved.</param>
public sealed record LoadOrderLine(
    string Name,
    LoadOrderLineOutcome Outcome,
    Guid? ContainerId,
    string? MatchedBaseName,
    string? DisplayName);

/// <summary>
/// The immutable reconciliation plan over a parsed load-order file: one row
/// per file line (file order), plus the derived projections the apply path
/// consumes. Built by <see cref="LoadOrderPlanner"/>; carries no behavior.
/// </summary>
public sealed class LoadOrderPlan
{
    /// <summary>The plan for an empty (comment-only) file: no rows, no order, nothing unmatched.</summary>
    public static LoadOrderPlan Empty { get; } = new(Array.Empty<LoadOrderLine>());

    /// <summary>Every file line's row, in file order (the review table renders these one-to-one).</summary>
    public IReadOnlyList<LoadOrderLine> Lines { get; }

    /// <summary>
    /// Every matched container id in file order (reorder + library-add rows
    /// alike): the <see cref="IProfileService.SetModOrder"/> argument. The
    /// ids of library adds that are not yet profile members are included;
    /// SetModOrder ignores unknown ids today, and the apply paths that
    /// import or associate content place those mods at their file
    /// positions deliberately. Installed-but-
    /// unlisted mods are NOT appended here; SetModOrder's own semantics keep
    /// them in relative order after the listed block.
    /// </summary>
    public IReadOnlyList<Guid> OrderedContainerIds { get; }

    /// <summary>The library-add rows in file order (repo-resident, not in the profile).</summary>
    public IReadOnlyList<LoadOrderLine> LibraryAdds { get; }

    /// <summary>The unresolved rows in file order (no local match, or an ambiguous one).</summary>
    public IReadOnlyList<LoadOrderLine> UnmatchedNames { get; }

    private LoadOrderPlan(IReadOnlyList<LoadOrderLine> lines)
    {
        Lines = lines;
        OrderedContainerIds = lines
            .Where(l => l.ContainerId is { })
            .Select(l => l.ContainerId!.Value)
            .ToArray();
        LibraryAdds = lines
            .Where(l => l.Outcome == LoadOrderLineOutcome.LibraryAdd)
            .ToArray();
        UnmatchedNames = lines
            .Where(l => l.Outcome == LoadOrderLineOutcome.Unresolved)
            .ToArray();
    }

    /// <summary>
    /// Builds a plan from reconciled rows, deriving the order/adds/unmatched
    /// projections. Internal: callers go through <see cref="LoadOrderPlanner"/>.
    /// </summary>
    internal static LoadOrderPlan FromLines(IEnumerable<LoadOrderLine> lines) =>
        new(lines.ToArray());
}

/// <summary>
/// Pure load-order reconciliation: matches a parsed file's names against the
/// active profile's mods and the repository's non-profile containers
/// (both sides injected as already-resolved data, so this helper stays free
/// of IO and repository knowledge) and produces the immutable
/// <see cref="LoadOrderPlan"/>. Stateless + Avalonia-free so the matching
/// rules are unit-testable independently of the caller (the mod-list
/// reorder-planner style, hosted in Profiles where the base-name + order
/// semantics live).
/// </summary>
/// <remarks>
/// <para><b>Matching</b> is case-insensitive ordinal on the base folder
/// name. A profile match wins outright (a profile cannot hold two mods with
/// the same base name; the import-time collision block enforces it). A name
/// with no profile match resolves against the repository candidates.</para>
/// <para><b>Ambiguity</b> (two repo candidates sharing the base name, which
/// the repository permits across sources): the Nexus-sourced candidate is
/// preferred; if that still leaves more than one (two Nexus containers, or
/// two untracked), the line goes to <see cref="LoadOrderPlan.UnmatchedNames">
/// unmatched</see> rather than being silently picked.</para>
/// <para><b>Locks are not reasoned about here.</b> Order application is a
/// single <see cref="IProfileService.SetModOrder"/> call whose own lock
/// projection keeps every locked entry at its exact index (a locked DMF at
/// rank 0 mirrors DML's force-insert of <c>dmf</c> at rank 1), so the planner
/// supplies only the listed prefix.</para>
/// </remarks>
public static class LoadOrderPlanner
{
    /// <summary>
    /// Builds the plan for the given names against the two resolved sides.
    /// </summary>
    /// <param name="names">The parsed file names, in file order
    /// (already deduplicated by <see cref="ModLoadOrderParser"/>).</param>
    /// <param name="profileMods">The active profile's mods with resolved base
    /// names (one per entry; unresolvable entries omitted by the caller).</param>
    /// <param name="repoCandidates">The repository's containers that are not
    /// in the profile, with resolved base names (unresolvable containers
    /// omitted by the caller).</param>
    public static LoadOrderPlan Build(
        IReadOnlyList<string> names,
        IReadOnlyList<LoadOrderProfileMod> profileMods,
        IReadOnlyList<LoadOrderRepoCandidate> repoCandidates)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(profileMods);
        ArgumentNullException.ThrowIfNull(repoCandidates);

        if (names.Count == 0)
        {
            return LoadOrderPlan.Empty;
        }

        // Case-insensitive ordinal on the base folder name. First entry wins
        // on a (hand-edited) duplicate profile base name: deterministic, and
        // the collision invariant makes the case pathological.
        var byProfileBaseName = new Dictionary<string, LoadOrderProfileMod>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var mod in profileMods)
        {
            byProfileBaseName.TryAdd(mod.BaseName, mod);
        }

        var candidatesByBaseName = new Dictionary<string, List<LoadOrderRepoCandidate>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in repoCandidates)
        {
            if (!candidatesByBaseName.TryGetValue(candidate.BaseName, out var list))
            {
                list = new List<LoadOrderRepoCandidate>();
                candidatesByBaseName[candidate.BaseName] = list;
            }

            list.Add(candidate);
        }

        var lines = new List<LoadOrderLine>(names.Count);
        foreach (var name in names)
        {
            if (byProfileBaseName.TryGetValue(name, out var profileMod))
            {
                lines.Add(new LoadOrderLine(
                    name, LoadOrderLineOutcome.Reorder,
                    profileMod.ContainerId, profileMod.BaseName, profileMod.DisplayName));
                continue;
            }

            if (candidatesByBaseName.TryGetValue(name, out var candidates))
            {
                // Ambiguity: prefer the Nexus-sourced candidate; a remaining
                // tie (two Nexus, or two untracked) is reported unmatched
                // rather than silently picked.
                var resolved = candidates.Count == 1
                    ? candidates[0]
                    : candidates.Where(c => c.IsNexusSourced).ToArray() is { Length: 1 } nexus
                        ? nexus[0]
                        : null;
                if (resolved is not null)
                {
                    lines.Add(new LoadOrderLine(
                        name, LoadOrderLineOutcome.LibraryAdd,
                        resolved.ContainerId, resolved.BaseName, resolved.DisplayName));
                    continue;
                }
            }

            lines.Add(new LoadOrderLine(name, LoadOrderLineOutcome.Unresolved, null, null, null));
        }

        return LoadOrderPlan.FromLines(lines);
    }
}
