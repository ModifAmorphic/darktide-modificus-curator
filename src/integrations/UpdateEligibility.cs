using Modificus.Curator.Mods;

namespace Modificus.Curator.Integrations;

/// <summary>
/// The single source of the four known-update eligibility rules, shared by the
/// hydration self-heal (<see cref="IUpdateStateStore"/>) and the install-time
/// revalidation (the mod-update installer). A persisted or flagged entry stays
/// eligible only while the live candidate + container still match what the
/// flag was recorded against.
/// </summary>
/// <remarks>
/// Pure + static: no service dependencies, no I/O, no clock. Everything the
/// evaluator needs arrives as arguments so both call sites (hydration over
/// persisted snapshots, install over a check result's entry) evaluate the
/// identical rules.
/// </remarks>
public static class UpdateEligibility
{
    /// <summary>
    /// Evaluates the four rules in order: the container is still a member of
    /// the candidate list (<paramref name="candidate"/> non-null), the entry
    /// is still on <see cref="LatestPolicy"/>, the container still resolves to
    /// a <see cref="NexusSource"/> with <paramref name="expectedModId"/>, and
    /// the installed version (resolved via
    /// <see cref="ModContainer.ResolveVersion"/> with a
    /// <see cref="LatestPolicy"/>) still matches
    /// <paramref name="expectedVersion"/> case-insensitively.
    /// </summary>
    /// <param name="candidate">The candidate for the container the flag was
    /// recorded against, or <c>null</c> when it is no longer a member of the
    /// profile (removed).</param>
    /// <param name="container">The live container, or <c>null</c> when the
    /// repository no longer has it.</param>
    /// <param name="expectedModId">The Nexus mod id the flag was recorded
    /// against.</param>
    /// <param name="expectedVersion">The installed version string the flag was
    /// recorded against.</param>
    /// <param name="reason">When ineligible, a short machine-readable reason
    /// ("removed from profile", "re-pinned", "container gone", "source
    /// changed", "version changed"); empty when eligible.</param>
    /// <returns><c>true</c> when all four rules hold.</returns>
    public static bool IsEligible(
        ModListCandidate? candidate,
        ModContainer? container,
        int expectedModId,
        string expectedVersion,
        out string reason)
    {
        if (candidate is null)
        {
            reason = "removed from profile";
            return false;
        }

        if (candidate.Policy is not LatestPolicy)
        {
            reason = "re-pinned";
            return false;
        }

        if (container is null)
        {
            reason = "container gone";
            return false;
        }

        if (container.Source is not NexusSource nexus || nexus.ModId != expectedModId)
        {
            reason = "source changed";
            return false;
        }

        var installedVersion = container.ResolveVersion(new LatestPolicy())?.VersionString
            ?? string.Empty;
        if (!string.Equals(installedVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            reason = "version changed";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
