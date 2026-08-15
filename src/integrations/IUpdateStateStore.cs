using Modificus.Curator.General;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Owns the persistence rules for profile-scoped "known update available"
/// knowledge: when a fresh authoritative result replaces (or clears) a profile's
/// state, when prior state must be preserved, how to acknowledge a successful
/// local version change, and how to hydrate persisted state for the current
/// profile while filtering out stale entries.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IUpdateCheckService.LastResult"/> is memory-only, so a restart
/// inside the interval gate would show no flags until the next API call lands.
/// Persisting the flagged mods (via <see cref="IKnownUpdateState.KnownUpdates"/>)
/// keeps the flags visible across a restart; the data model carries the
/// persistence, this interface owns the domain rules over it.</para>
/// <para>
/// <b>Profile-scoped.</b> State is keyed by profile id. A result from profile A
/// never becomes profile B's state, so a single in-memory
/// <c>LastResult</c> cannot bleed across a profile switch.</para>
/// <para>
/// <b>Replacement rules.</b> <see cref="RecordResult"/> is the only writer that
/// replaces or clears state:
/// <list type="bullet">
/// <item><see cref="CheckOutcome.Success"/> replaces that profile's snapshot with
/// the result's flagged mods (clearing it when the API authoritatively reports no
/// updates).</item>
/// <item><see cref="CheckOutcome.NoNexusMods"/> clears that profile's snapshot
/// (local state proves no applicable Nexus update can exist).</item>
/// <item><see cref="CheckOutcome.NoAuth"/>, <see cref="CheckOutcome.RateLimited"/>,
/// and <see cref="CheckOutcome.Failed"/> preserve prior state (the user may have
/// signed out, or the check was transiently blocked; what was flagged before is
/// still the best knowledge).</item>
/// </list>
/// Restored/persisted data is never re-published as a fresh authoritative result;
/// it is read directly to render flags.</para>
/// <para>
/// <b>Hydration self-heals.</b> <see cref="GetKnownUpdateContainerIds"/> reads the
/// persisted snapshots for a profile + filters out entries whose membership,
/// policy, source, or installed version no longer match. Removed (no longer in
/// the profile), pinned (policy changed to Pinned), source-changed (no longer a
/// Nexus mod with the same id), or version-changed (a local version change since
/// the flag was recorded) entries are dropped and the filtered set is written
/// back so the next read is fast.</para>
/// <para>
/// <b>Acknowledgement.</b> <see cref="AcknowledgeInstall"/> removes a single
/// profile/container entry immediately after a successful local version change,
/// so a just-installed version clears its own flag without waiting for the next
/// API check.</para>
/// </remarks>
public interface IUpdateStateStore
{
    /// <summary>
    /// Applies the replacement rules for <paramref name="profileId"/> based on the
    /// result's <see cref="UpdateCheckResult.Outcome"/>. Authoritative success
    /// replaces that profile's snapshot (clearing when the API reports no
    /// updates); a no-Nexus-mods outcome clears it; every other outcome preserves
    /// prior state.
    /// </summary>
    /// <param name="profileId">The profile the check ran against.</param>
    /// <param name="result">The check result (carries the outcome + the flagged
    /// mods on a success).</param>
    void RecordResult(Guid profileId, UpdateCheckResult result);

    /// <summary>
    /// Removes the known-update entry for <paramref name="containerId"/> in
    /// <paramref name="profileId"/> immediately, so a just-installed version
    /// clears its own flag without an extra API check. A no-op when no entry
    /// exists for that container.
    /// </summary>
    void AcknowledgeInstall(Guid profileId, Guid containerId);

    /// <summary>
    /// Reads the persisted known-update entries for <paramref name="profileId"/>,
    /// filters out stale ones (removed / pinned / source-changed / version-changed
    /// since the flag was recorded), writes the filtered set back, and returns the
    /// container ids that remain flagged. Rendering is always keyed by the current
    /// profile's authoritative-or-persisted state.
    /// </summary>
    /// <param name="profileId">The profile whose known updates to hydrate.</param>
    /// <returns>The flagged container ids for the profile, after self-healing. An
    /// unknown profile or one with no recorded entries yields an empty
    /// collection.</returns>
    IReadOnlyCollection<Guid> GetKnownUpdateContainerIds(Guid profileId);
}
