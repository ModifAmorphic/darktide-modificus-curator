using System.ComponentModel;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Session;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="ProfileSession"/> unit tests: the can-change gate (RequestActive),
/// the delete gate (CanDeleteProfile), delete-of-active recovery (ReconcileActive
/// clears the active id), persistence, and the live running-state refresh. All
/// against in-memory fakes; the polling timer is injected as null and
/// <see cref="ProfileSession.Refresh"/> is driven directly for deterministic
/// running-state changes.
/// </summary>
public sealed class ProfileSessionTests
{
    private static ProfileSession Build(
        FakeSteamService? steam = null,
        FakeProfileService? profiles = null,
        FakeAppStateStore? appState = null)
    {
        steam ??= new FakeSteamService();
        profiles ??= TestDoubles.Profiles();
        appState ??= new FakeAppStateStore();
        // startTimer = null: no polling; tests drive Refresh() directly.
        return new ProfileSession(steam, profiles, appState, startTimer: null);
    }

    private static ProfileSummary Profile(string name) => new(Guid.NewGuid(), name, "");

    // ---- construction / restore -------------------------------------------

    [Fact]
    public void Constructor_restores_the_persisted_active_id()
    {
        var a = Profile("Alpha");
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };

        var session = Build(profiles: TestDoubles.Profiles(a), appState: appState);

        Assert.Equal(a.Id, session.ActiveProfileId);
    }

    [Fact]
    public void Constructor_leaves_active_null_when_none_recorded()
    {
        var session = Build(
            profiles: TestDoubles.Profiles(Profile("Alpha")),
            appState: new FakeAppStateStore { ActiveProfileId = null });

        Assert.Null(session.ActiveProfileId);
    }

    [Fact]
    public void Constructor_does_not_persist_when_restoring_the_active_id()
    {
        // Restore goes straight into the field (no write-back), even for a valid id.
        var a = Profile("Alpha");
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };

        var session = Build(profiles: TestDoubles.Profiles(a), appState: appState);

        Assert.Equal(0, appState.SetCount);
    }

    [Fact]
    public void Constructor_snapshots_the_initial_running_state()
    {
        var steam = new FakeSteamService { Running = true };

        var session = Build(steam: steam);

        Assert.True(session.IsRunning);
    }

    // ---- RequestActive: the sole gate -------------------------------------

    [Fact]
    public void RequestActive_applies_and_persists_when_not_running()
    {
        var a = Profile("Alpha");
        var appState = new FakeAppStateStore();
        var session = Build(profiles: TestDoubles.Profiles(a), appState: appState);
        session.IsRunning = false;

        session.RequestActive(a.Id);

        Assert.Equal(a.Id, session.ActiveProfileId);
        Assert.Equal(a.Id, appState.ActiveProfileId);
    }

    [Fact]
    public void RequestActive_is_a_noop_when_running()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        var session = Build(profiles: TestDoubles.Profiles(a, b), appState: appState);
        session.IsRunning = true;

        session.RequestActive(b.Id);

        Assert.Equal(a.Id, session.ActiveProfileId);   // unchanged
        Assert.Equal(a.Id, appState.ActiveProfileId);   // not persisted to b
    }

    [Fact]
    public void RequestActive_to_the_current_id_is_a_noop_persist()
    {
        var a = Profile("Alpha");
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        var session = Build(profiles: TestDoubles.Profiles(a), appState: appState);
        session.IsRunning = false;

        session.RequestActive(a.Id);

        Assert.Equal(0, appState.SetCount); // same value, no write
    }

    // ---- CanDeleteProfile: the delete gate --------------------------------

    [Fact]
    public void CanDeleteProfile_locks_the_active_id_while_running()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        var session = Build(profiles: TestDoubles.Profiles(a, b), appState: appState);
        session.IsRunning = true;

        Assert.False(session.CanDeleteProfile(a.Id)); // active locked while running
    }

    [Fact]
    public void CanDeleteProfile_allows_a_non_active_profile_while_running()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        var session = Build(profiles: TestDoubles.Profiles(a, b), appState: appState);
        session.IsRunning = true;

        Assert.True(session.CanDeleteProfile(b.Id)); // non-active deletable anytime
    }

    [Fact]
    public void CanDeleteProfile_allows_the_active_id_when_not_running()
    {
        var a = Profile("Alpha");
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        var session = Build(profiles: TestDoubles.Profiles(a), appState: appState);
        session.IsRunning = false;

        Assert.True(session.CanDeleteProfile(a.Id));
    }

    [Fact]
    public void CanDeleteProfile_allows_every_profile_when_none_is_active()
    {
        var a = Profile("Alpha");
        var session = Build(
            profiles: TestDoubles.Profiles(a),
            appState: new FakeAppStateStore { ActiveProfileId = null });
        session.IsRunning = true;

        Assert.True(session.CanDeleteProfile(a.Id)); // nothing locked when none active
    }

    // ---- ReconcileActive: delete-of-active recovery -----------------------

    [Fact]
    public void ReconcileActive_clears_active_when_the_active_profile_is_deleted()
    {
        // Delete-of-active is blocked while the game runs (CanDeleteProfile), so this
        // path runs when stopped. The active clears (null): we never auto-select a
        // remaining profile on someone's behalf; the user explicitly picks the next.
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        var session = Build(profiles: profiles, appState: appState);
        session.IsRunning = false;

        profiles.DeleteProfile(a.Id);
        session.ReconcileActive();

        Assert.Null(session.ActiveProfileId); // not b.Id: cleared, not switched
        Assert.Null(appState.ActiveProfileId);
    }

    [Fact]
    public void ReconcileActive_clears_active_when_no_profiles_remain()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        var session = Build(profiles: profiles, appState: appState);

        profiles.DeleteProfile(a.Id);
        session.ReconcileActive();

        Assert.Null(session.ActiveProfileId);
        Assert.Null(appState.ActiveProfileId);
    }

    [Fact]
    public void ReconcileActive_is_a_noop_when_the_active_profile_still_exists()
    {
        var a = Profile("Alpha");
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        var session = Build(profiles: TestDoubles.Profiles(a, Profile("Bravo")), appState: appState);

        session.ReconcileActive();

        Assert.Equal(a.Id, session.ActiveProfileId); // still here, no change
    }

    [Fact]
    public void ReconcileActive_is_a_noop_when_no_active_is_set()
    {
        // First run / nothing chosen: never auto-select a profile on someone's behalf.
        var a = Profile("Alpha");
        var session = Build(
            profiles: TestDoubles.Profiles(a),
            appState: new FakeAppStateStore { ActiveProfileId = null });

        session.ReconcileActive();

        Assert.Null(session.ActiveProfileId);
    }

    // ---- live running-state refresh ---------------------------------------

    [Fact]
    public void Refresh_updates_IsRunning_from_the_steam_service()
    {
        var steam = new FakeSteamService { Running = false };
        var session = Build(steam: steam);
        Assert.False(session.IsRunning);

        steam.Running = true;
        session.Refresh();

        Assert.True(session.IsRunning);

        steam.Running = false;
        session.Refresh();

        Assert.False(session.IsRunning);
    }

    [Fact]
    public void IsRunning_change_raises_property_changed()
    {
        // The shell relies on this to mirror live running-state.
        var session = Build(steam: new FakeSteamService { Running = false });
        var raised = new List<string?>();
        ((INotifyPropertyChanged)session).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        session.IsRunning = true;

        Assert.Contains(nameof(IProfileSession.IsRunning), raised);
    }

    [Fact]
    public void ActiveProfileId_change_raises_property_changed_and_persists()
    {
        var a = Profile("Alpha");
        var appState = new FakeAppStateStore();
        var session = Build(profiles: TestDoubles.Profiles(a), appState: appState);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)session).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        session.RequestActive(a.Id);

        Assert.Contains(nameof(IProfileSession.ActiveProfileId), raised);
        Assert.Equal(a.Id, appState.ActiveProfileId);
    }
}
