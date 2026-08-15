using Modificus.Curator.Integrations;
using Modificus.Curator.Profiles;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The one place the UI maps its profile mod-list entries to the Integrations
/// update family's candidate shape. The UI layer references both libraries, so
/// the mapping lives on the consumer side of the boundary and Integrations
/// holds no Profiles dependency.
/// </summary>
internal static class ModListCandidateExtensions
{
    /// <summary>
    /// Maps profile mod-list entries to update candidates (the container id +
    /// the entry's current policy, the two fields the update family operates
    /// on). The mapping is total: membership is the caller's problem, and an
    /// empty list maps to an empty list.
    /// </summary>
    internal static IReadOnlyList<ModListCandidate> ToCandidates(
        this IReadOnlyList<ModListEntry> entries) =>
        entries.Select(e => new ModListCandidate(e.ContainerId, e.Policy)).ToArray();
}
