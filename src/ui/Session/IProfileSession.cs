using System.ComponentModel;
using Modificus.Curator.Profiles;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The single authority for "which profile is active, can it change, and is the
/// game running." Owns the active id (observable + persisted), the can-change
/// gate, and the LIVE running-state. Does NOT own profile CRUD
/// (create/rename/delete stay on <see cref="IProfileService"/>); it only owns
/// active state, the gate, and running-state.
/// </summary>
/// <remarks>
/// <para><b>The gate lives here, once:</b> <see cref="RequestActive"/> is the
/// sole place a voluntary active change is allowed or rejected (applied only
/// when the game isn't running), so every active-change path routes through it
/// and the two can never diverge.</para>
/// <para><b>Delete-of-active:</b> the active profile is locked while Darktide runs
/// (<see cref="CanDeleteProfile"/> gates the delete), so delete-of-active only
/// happens when the game is stopped. <see cref="ReconcileActive"/> then clears the
/// active id (null) so the user explicitly picks the next; it never auto-selects a
/// remaining profile on someone's behalf.</para>
/// <para><b>Observable:</b> implements <see cref="INotifyPropertyChanged"/> so
/// live <see cref="IsRunning"/> changes (driven by the session's polling timer)
/// react within a few seconds of the game starting or stopping.</para>
/// </remarks>
public interface IProfileSession : INotifyPropertyChanged
{
    /// <summary>The current active profile id, or null when none is active.</summary>
    Guid? ActiveProfileId { get; }

    /// <summary>
    /// Whether Darktide is currently running. LIVE, refreshed by a polling timer
    /// (~3s, cheap process scan).
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Session-scoped edit/stage coordination state: <c>true</c> when the active
    /// profile has structural/version edits not yet reflected in the staged tree
    /// the running game loaded. Set by mod-list edits; cleared on the next
    /// successful stage (a launch). In-memory only (never persisted).
    /// </summary>
    bool HasPendingChanges { get; set; }

    /// <summary>
    /// The SOLE active-change gate. Requests <paramref name="id"/> as the active
    /// profile: applied + persisted only when the game isn't running; otherwise a
    /// no-op (the active stays put). Rename and delete-of-active do not route
    /// through this (rename leaves the id stable; delete uses
    /// <see cref="ReconcileActive"/>).
    /// </summary>
    void RequestActive(Guid id);

    /// <summary>
    /// Whether the profile <paramref name="id"/> may be deleted right now. The
    /// single authority for the delete gate: the active profile is locked while
    /// Darktide runs (<c>false</c> when <paramref name="id"/> is the active id and
    /// the game is running); every other profile is deletable (<c>true</c>).
    /// </summary>
    bool CanDeleteProfile(Guid id);

    /// <summary>
    /// Recovery after CRUD that may have removed the active profile: if the current
    /// active id no longer exists in <see cref="IProfileService.ListProfiles"/>,
    /// clears the active id (null) and persists. Delete-of-active is blocked while
    /// the game runs (<see cref="CanDeleteProfile"/>), so by the time this runs the
    /// game is stopped and null is the correct outcome (the user explicitly picks
    /// the next profile; we never auto-select a remaining one). A no-op when the
    /// active id is still present, or when no active is set (first run / nothing
    /// chosen).
    /// </summary>
    void ReconcileActive();

    /// <summary>
    /// Re-checks <see cref="IsRunning"/> against the running-state source right
    /// now, rather than waiting for the next polling-timer tick. For callers that
    /// just caused a state change (e.g. after a successful launch) so the
    /// indicator reacts immediately. The polling timer would catch up eventually;
    /// this is the eager path.
    /// </summary>
    void Refresh();
}
