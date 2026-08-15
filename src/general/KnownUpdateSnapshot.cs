namespace Modificus.Curator.General;

/// <summary>
/// One persisted "known update available" entry for a mod, scoped to a profile.
/// A plain serializable DTO (no domain behavior) so <see cref="IKnownUpdateState"/>
/// can persist it in <c>app-state.json</c> without the General library taking a
/// dependency on the Integrations update-check domain. The Integrations-layer
/// <c>IUpdateStateStore</c> owns the domain rules (when to record, when to clear,
/// how to filter on hydration); this record is just the persisted shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every field.</b> A snapshot identifies the flagged mod well enough to
/// invalidate stale knowledge after a local version change without re-querying
/// Nexus. <see cref="ProfileId"/> scopes the entry (a known update for profile A
/// is never treated as profile B's state); <see cref="ContainerId"/> is the join
/// key back to the profile entry + the repository; <see cref="ModId"/> is the
/// Nexus mod identity (cross-checks a source change); <see cref="CurrentVersion"/>
/// is the installed version at flag time (a local version change invalidates the
/// flag); <see cref="CheckedAt"/> + <see cref="LatestUpdateAt"/> are bookkeeping
/// (when the flag was recorded + the mod's last Nexus update time).</para>
/// <para>
/// <b>Display names are not persisted here.</b> A snapshot carries only what is
/// needed to identify + invalidate; the row's display name continues to come from
/// repository persistence, not the snapshot. This keeps the persisted record small
/// and avoids a stale-name drift between the snapshot and the repository.</para>
/// <para>
/// <b>Restored data is not authoritative.</b> A snapshot loaded from disk is
/// prior knowledge, not a fresh API result. The Integrations layer never publishes
/// restored snapshots as an <c>UpdateCheckResult</c>; the UI reads them directly
/// to render flags while a real check is in flight or when the interval gate
/// suppresses the opening check.</para>
/// </remarks>
/// <param name="ProfileId">The profile this known update belongs to (the
/// persistence scope; entries never bleed across profiles).</param>
/// <param name="ContainerId">The flagged mod's container id (the join key back to
/// the profile entry + <c>ModContainer</c>).</param>
/// <param name="ModId">The flagged mod's Nexus mod id (from
/// <c>NexusSource.ModId</c>; cross-checks a source change on hydration).</param>
/// <param name="CurrentVersion">The installed version's display tag at flag time
/// (from <c>ModVersion.VersionString</c>). A local version change (re-import,
/// update, nxm acquisition) invalidates the flag because the snapshot no longer
/// matches what is installed.</param>
/// <param name="CheckedAt">When the authoritative check that recorded this flag
/// ran (UTC).</param>
/// <param name="LatestUpdateAt">The mod's last update time on Nexus (UTC) at flag
/// time, from the v2 <c>updatedAt</c> field. Bookkeeping; may be null when the
/// server did not report it.</param>
public sealed record KnownUpdateSnapshot(
    Guid ProfileId,
    Guid ContainerId,
    int ModId,
    string CurrentVersion,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LatestUpdateAt);
