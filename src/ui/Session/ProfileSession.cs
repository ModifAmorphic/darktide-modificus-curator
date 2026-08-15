using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.General;
using Modificus.Curator.Profiles;
using Modificus.Curator.Steam;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// Production <see cref="IProfileSession"/>. Owns the active id (restored from
/// <see cref="IProfileActivationState"/> at startup, persisted on every change), the
/// can-change gate (<see cref="RequestActive"/>), and the live running-state (a
/// <see cref="DispatcherTimer"/> polling <see cref="ISteamService.IsGameRunning"/>
/// roughly every 3 seconds; a cheap process scan that catches external game
/// start/stop while Curator is open).
/// </summary>
/// <remarks>
/// <para><b>Testability:</b> the polling timer is injected as a
/// <paramref name="startTimer"/> delegate, so unit tests construct the session
/// without a UI dispatcher and drive <see cref="Refresh"/> directly for
/// deterministic running-state changes. The session's own logic (gate,
/// persistence, fallback) has no time dependency and no Avalonia dependency.</para>
/// </remarks>
public sealed partial class ProfileSession : ObservableObject, IProfileSession
{
    /// <summary>The polling interval for the live running-state.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly ISteamService _steam;
    private readonly IProfileService _profiles;
    private readonly IProfileActivationState _appState;

    /// <param name="steam">The running-state source (<see cref="IsGameRunning"/>).</param>
    /// <param name="profiles">Used by <see cref="ReconcileActive"/> to detect delete-of-active.</param>
    /// <param name="appState">Where the active id is persisted across restarts.</param>
    /// <param name="startTimer">Starts the periodic running-state poll. Production
    /// wires this to a <see cref="DispatcherTimer"/> (UI thread); tests pass null
    /// and call <see cref="Refresh"/> directly for deterministic changes.</param>
    public ProfileSession(
        ISteamService steam,
        IProfileService profiles,
        IProfileActivationState appState,
        Action<Action>? startTimer = null)
    {
        _steam = steam;
        _profiles = profiles;
        _appState = appState;

        // Restore the persisted active id straight into the field (no write-back,
        // no subscribers yet). A stale id (deleted while Curator was closed) resolves
        // to no selection in the shell; it is cleaned up lazily on the next
        // delete-of-active reconcile rather than rewritten at startup.
        _activeProfileId = appState.ActiveProfileId;

        // Snapshot the initial running-state, then start the live poll.
        Refresh();
        startTimer?.Invoke(Refresh);
    }

    /// <summary>The current active profile id. Persisted on every change.</summary>
    [ObservableProperty]
    private Guid? _activeProfileId;

    /// <summary>Persists the active id whenever it changes.</summary>
    partial void OnActiveProfileIdChanged(Guid? value)
    {
        _appState.ActiveProfileId = value;
    }

    /// <summary>LIVE running-state, refreshed by the polling timer.</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>
    /// Session-scoped edit/stage coordination state. See
    /// <see cref="IProfileSession.HasPendingChanges"/>.
    /// </summary>
    [ObservableProperty]
    private bool _hasPendingChanges;

    /// <summary>
    /// The SOLE active-change gate. Applies + persists only when the game isn't
    /// running; otherwise a no-op. See <see cref="IProfileSession.RequestActive"/>.
    /// </summary>
    public void RequestActive(Guid id)
    {
        if (IsRunning)
        {
            return;
        }

        ActiveProfileId = id;
    }

    /// <summary>
    /// The delete gate. See <see cref="IProfileSession.CanDeleteProfile"/>.
    /// </summary>
    public bool CanDeleteProfile(Guid id) => !(id == ActiveProfileId && IsRunning);

    /// <summary>
    /// Recovery after delete-of-active. See
    /// <see cref="IProfileSession.ReconcileActive"/>.
    /// </summary>
    public void ReconcileActive()
    {
        // Only recover when an active id was set but no longer exists. Delete-of-active
        // is blocked while the game runs (CanDeleteProfile), so reaching here means the
        // game is stopped and the active should clear: the user explicitly picks the
        // next profile, we never auto-select a remaining one on someone's behalf.
        if (ActiveProfileId is not Guid id)
        {
            return;
        }

        var existing = _profiles.ListProfiles();
        if (existing.Any(p => p.Id == id))
        {
            return;
        }

        ActiveProfileId = null;
    }

    /// <summary>
    /// Re-checks <see cref="ISteamService.IsGameRunning"/> and updates
    /// <see cref="IsRunning"/>. Invoked by the polling timer; exposed so unit
    /// tests drive running-state changes deterministically without real time.
    /// </summary>
    public void Refresh()
    {
        IsRunning = _steam.IsGameRunning();
    }
}
