using Modificus.Curator.Mods;

namespace Modificus.Curator.Integrations;

/// <summary>
/// One mod-list entry the update family operates on: the container id + the
/// profile's current version policy for it. The caller (the UI layer, which
/// references both libraries) maps its profile entries to candidates at the
/// call site, so Integrations needs no Profiles dependency; the profile id
/// carried alongside the candidates remains only the update-state key.
/// </summary>
/// <param name="ContainerId">The mod's container id (the join key to
/// <see cref="ModContainer"/> and the profile entry).</param>
/// <param name="Policy">The profile's current version policy for the mod
/// (<see cref="LatestPolicy"/> entries are the flaggable/installable subset;
/// <see cref="PinnedPolicy"/> entries ride along for name sync only).</param>
public sealed record ModListCandidate(Guid ContainerId, ModVersionPolicy Policy);
