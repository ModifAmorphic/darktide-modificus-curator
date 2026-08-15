namespace Modificus.Curator.Integrations;

/// <summary>
/// The single mod-update install path: acquire the latest Nexus release for a
/// flagged container under the global one-install-at-a-time gate, acknowledge
/// the install on success, and report per-container progress. Both callers of
/// the Premium install flow (the manual per-row update action + the automatic
/// Premium batch) route through this interface, so the two paths can never
/// install concurrently and share one acknowledge + progress contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gating.</b> The shared <see cref="UpdateCoordinator"/> serializes
/// installs globally. <see cref="TryInstallLatestAsync"/> (the manual
/// semantics) refuses politely when the gate is held (a
/// <see cref="ModInstallStatus.Busy"/> outcome, nothing touched);
/// <see cref="InstallLatestAsync"/> (the automatic semantics) awaits its turn
/// so a sequential batch proceeds one mod at a time.</para>
/// <para>
/// <b>In-gate revalidation.</b> Both methods revalidate eligibility inside the
/// gate via <see cref="UpdateEligibility"/> against the caller's candidates: a
/// stale flag (the mod was removed, re-pinned, source-changed, or already
/// version-changed since the flag was recorded) yields
/// <see cref="ModInstallStatus.NotEligible"/> + the reason, with nothing
/// installed. This replaces caller-side revalidation: the check runs under the
/// gate so the decision + the install observe the same serialized state.</para>
/// <para>
/// <b>Acknowledge on success only, exactly once.</b> A successful acquisition
/// is followed immediately by <see cref="IUpdateStateStore.AcknowledgeInstall"/>
/// (clearing the persisted known-update entry without an extra API check). A
/// busy, ineligible, failed, or cancelled attempt never acknowledges.</para>
/// <para>
/// <b>Progress.</b> <see cref="ModUpdateProgress"/> is raised with
/// <c>active = true</c> when an install attempt starts (after the gate is
/// acquired) and <c>active = false</c> from the attempt's finally block
/// (success, failure, or cancellation), so a subscriber's spinner can never get
/// stuck. <see cref="BusyChanged"/> mirrors the coordinator for subscribers
/// that gate other affordances on "an install is in flight".</para>
/// <para>
/// <b>Errors.</b> A non-cancellation failure surfaces as
/// <see cref="ModInstallStatus.Failed"/> carrying the exception (the caller
/// decides how to present it); <see cref="OperationCanceledException"/>
/// propagates rather than becoming an outcome, so cancellation keeps its
/// caller-side swallow posture.</para>
/// </remarks>
public interface IModUpdateInstaller
{
    /// <summary>
    /// Whether an install is currently in flight (coordinator-backed). Raised
    /// via <see cref="BusyChanged"/> on acquire + release.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// Raised (on the acquiring/releasing thread) when <see cref="IsBusy"/>
    /// changes. Subscribers marshal to the UI thread if they touch UI state.
    /// </summary>
    event EventHandler? BusyChanged;

    /// <summary>
    /// Raised around each install attempt: <c>active = true</c> immediately
    /// after the gate is acquired (before the eligibility check + the
    /// acquisition), <c>active = false</c> from the attempt's finally block
    /// (regardless of the outcome). Deterministic start/stop ordering per
    /// serialized attempt. An event for a container no longer in the caller's
    /// UI (after a profile switch) is the subscriber's to ignore.
    /// </summary>
    event EventHandler<ModUpdateProgressEventArgs>? ModUpdateProgress;

    /// <summary>
    /// The MANUAL install semantics: non-blocking gate acquisition. When
    /// another install holds the gate, returns
    /// <see cref="ModInstallStatus.Busy"/> without touching anything (a clean
    /// no-op for the caller). Otherwise behaves exactly like
    /// <see cref="InstallLatestAsync"/>.
    /// </summary>
    /// <param name="profileId">The profile whose known-update entry is
    /// acknowledged on success (the update-state key).</param>
    /// <param name="containerId">The container to update.</param>
    /// <param name="modId">The Nexus mod id to acquire.</param>
    /// <param name="expectedVersion">The installed version the flag was
    /// recorded against (the eligibility version rule compares it to the
    /// container's current resolved version).</param>
    /// <param name="candidates">The profile's current mod-list entries, for
    /// the in-gate eligibility revalidation.</param>
    /// <param name="ct">Cancellation token. <see cref="OperationCanceledException"/>
    /// propagates (cancellation is not an outcome).</param>
    Task<ModInstallOutcome> TryInstallLatestAsync(
        Guid profileId,
        Guid containerId,
        int modId,
        string expectedVersion,
        IReadOnlyList<ModListCandidate> candidates,
        CancellationToken ct = default);

    /// <summary>
    /// The AUTOMATIC install semantics: awaits the gate (a sequential batch
    /// stays ordered, waiting its turn behind a manual install). Otherwise
    /// behaves exactly like <see cref="TryInstallLatestAsync"/>.
    /// </summary>
    /// <param name="profileId">The profile whose known-update entry is
    /// acknowledged on success (the update-state key).</param>
    /// <param name="containerId">The container to update.</param>
    /// <param name="modId">The Nexus mod id to acquire.</param>
    /// <param name="expectedVersion">The installed version the flag was
    /// recorded against.</param>
    /// <param name="candidates">The profile's current mod-list entries, for
    /// the in-gate eligibility revalidation.</param>
    /// <param name="ct">Cancellation token. <see cref="OperationCanceledException"/>
    /// propagates (cancellation is not an outcome).</param>
    Task<ModInstallOutcome> InstallLatestAsync(
        Guid profileId,
        Guid containerId,
        int modId,
        string expectedVersion,
        IReadOnlyList<ModListCandidate> candidates,
        CancellationToken ct = default);
}

/// <summary>
/// The result of one install attempt. The shape is data, not control flow: the
/// caller switches on <see cref="Status"/> and reads what it needs (the manual
/// alert shows <see cref="Exception"/>.Message on
/// <see cref="ModInstallStatus.Failed"/>; the automatic batch logs
/// <see cref="Reason"/> for a skipped mod).
/// </summary>
/// <param name="Status">The outcome. <see cref="ModInstallStatus.Busy"/> only
/// from <see cref="IModUpdateInstaller.TryInstallLatestAsync"/> (the automatic
/// path awaits the gate instead).</param>
/// <param name="Reason">A short reason string: the eligibility rule that
/// rejected an ineligible attempt, or the failure's message on
/// <see cref="ModInstallStatus.Failed"/>. Empty when not applicable.</param>
/// <param name="Exception">The exception behind a
/// <see cref="ModInstallStatus.Failed"/> outcome, so the caller can surface the
/// exact message it always has. <c>null</c> on every other status (cancellation
/// propagates instead of becoming an outcome).</param>
public sealed record ModInstallOutcome(
    ModInstallStatus Status,
    string Reason = "",
    Exception? Exception = null);

/// <summary>The outcome of one install attempt.</summary>
public enum ModInstallStatus
{
    /// <summary>The acquisition + acknowledge completed; the flag cleared.</summary>
    Installed,

    /// <summary>The gate was held; nothing was touched (manual semantics).</summary>
    Busy,

    /// <summary>The in-gate eligibility revalidation rejected the target;
    /// nothing was installed or acknowledged.</summary>
    NotEligible,

    /// <summary>The acquisition (or acknowledge) failed; see the outcome's
    /// exception for the cause.</summary>
    Failed,
}

/// <summary>
/// Event payload for <see cref="IModUpdateInstaller.ModUpdateProgress"/>:
/// which container is being installed and whether the install is active
/// (starting) or inactive (done, whatever the outcome). Immutable.
/// </summary>
/// <param name="ContainerId">The container id of the mod being installed.</param>
/// <param name="IsActive"><c>true</c> when the install attempt is starting
/// (raised after the gate is acquired, before the eligibility check);
/// <c>false</c> when it finished (raised from the attempt's finally block,
/// regardless of success, failure, or cancellation).</param>
public sealed record ModUpdateProgressEventArgs(Guid ContainerId, bool IsActive);
