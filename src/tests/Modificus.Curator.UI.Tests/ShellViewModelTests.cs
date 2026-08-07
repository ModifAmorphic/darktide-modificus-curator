using Modificus.Curator.RelayClient;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Shell-VM profile controls: mirroring the session's active id + running-state,
/// dropdown switch (routed through the session gate), switch-blocked-while-running,
/// and dialog-driven list refresh. Track C adds the launch wiring tests: the
/// Launch command calls <see cref="IRelayLaunchService.Launch"/> + branches
/// on the result. All against in-memory fakes; the session is behind the
/// <see cref="UI.Session.IProfileSession"/> seam (a <see cref="FakeProfileSession"/>).
/// </summary>
public sealed class ShellViewModelTests
{
    private static readonly ILogger<ShellViewModel> Logger = NullLogger<ShellViewModel>.Instance;
    private static readonly LocalizationService Localization = new();

    private static ShellViewModel Build(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeDialogService? dialogs = null,
        FakeLaunchService? launch = null,
        DmfPromptService? dmfPrompts = null,
        FakeNxmHandlerRegistrar? nxmRegistrar = null,
        FakeAppUpdateService? appUpdate = null,
        FakeConfigLoader? configLoader = null)
    {
        profiles ??= TestDoubles.Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        // The DMF coordinator wires its event subscriptions to the supplied
        // fakes; constructed lazily so a test can pass its own (with seeded
        // state) for the DMF-prompt assertions.
        dmfPrompts ??= TestDoubles.BuildDmfPromptService(profiles, session);
        return new ShellViewModel(
            profiles,
            session,
            launch ?? new FakeLaunchService(),
            dialogs ?? new FakeDialogService(),
            dmfPrompts,
            Localization,
            TestDoubles.BuildModList(profiles, session),
            appUpdate ?? new FakeAppUpdateService(),
            invokeOnUi: static action => action(),
            Logger,
            configLoader ?? new FakeConfigLoader(),
            nxmRegistrar);
    }

    // ---- active-profile restore on construction ----------------------------

    [Fact]
    public void Constructor_restores_the_persisted_active_profile()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var session = new FakeProfileSession { ActiveProfileId = b.Id };

        var vm = Build(profiles, session);

        Assert.NotNull(vm.SelectedProfile);
        Assert.Equal(b.Id, vm.SelectedProfile!.Id);
    }

    [Fact]
    public void Constructor_leaves_selection_null_when_no_active_profile_recorded()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha"));
        var session = new FakeProfileSession { ActiveProfileId = null };

        var vm = Build(profiles, session);

        Assert.Null(vm.SelectedProfile);
    }

    [Fact]
    public void Constructor_does_not_request_an_active_change_during_restore()
    {
        // Restoring the saved active reads it from the session; it never asks the
        // session to change active (no voluntary gate invocation on startup).
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };

        var vm = Build(profiles, session);

        Assert.Equal(0, session.RequestActiveCalls);
    }

    [Fact]
    public void Constructor_falls_back_to_null_when_the_recorded_active_profile_is_absent()
    {
        // Stale state pointing at a deleted profile resolves to no selection.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha"));
        var session = new FakeProfileSession { ActiveProfileId = Guid.NewGuid() };

        var vm = Build(profiles, session);

        Assert.Null(vm.SelectedProfile);
    }

    // ---- dropdown switch routes through the session ------------------------

    [Fact]
    public void Setting_SelectedProfile_requests_the_id_active_through_the_session()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var vm = Build(profiles, session);

        vm.SelectedProfile = b;

        Assert.Equal(b.Id, session.ActiveProfileId); // session applied it
        Assert.Equal(1, session.RequestActiveCalls);
        Assert.Equal(b.Id, vm.SelectedProfile?.Id);  // selection follows the session
    }

    [Fact]
    public void Setting_SelectedProfile_reverts_when_the_session_gate_rejects()
    {
        // The session is the authority: when it blocks the change (game running),
        // the dropdown snaps back to the real active instead of lying.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = true };
        var vm = Build(profiles, session);

        vm.SelectedProfile = b; // programmatically (the dropdown is disabled while running)

        Assert.Equal(a.Id, vm.SelectedProfile?.Id); // reverted to the real active
        Assert.Equal(a.Id, session.ActiveProfileId); // session never moved
        Assert.Equal(1, session.RequestActiveCalls);  // but it was asked
    }

    // ---- switch-blocked-while-running --------------------------------------

    [Fact]
    public void CanSwitchProfile_is_true_when_not_running_and_profiles_exist()
    {
        var vm = Build(TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha")),
            new FakeProfileSession { IsRunning = false });

        Assert.True(vm.CanSwitchProfile);
    }

    [Fact]
    public void CanSwitchProfile_is_false_when_the_game_is_running()
    {
        var session = new FakeProfileSession { IsRunning = true };
        var vm = Build(TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha")), session);

        Assert.False(vm.CanSwitchProfile);
        Assert.Contains("Darktide is running", vm.ProfileSwitchTooltip);
    }

    [Fact]
    public void CanSwitchProfile_is_false_when_no_profiles_exist()
    {
        var vm = Build(TestDoubles.Profiles());

        Assert.False(vm.CanSwitchProfile);
        Assert.NotEmpty(vm.ProfileSwitchTooltip);
    }

    [Fact]
    public void Live_IsRunning_change_flips_CanSwitchProfile_and_the_tooltip()
    {
        // The status strip + dropdown-enable react to the session's live running-state
        // (the polling timer drives this in production).
        var session = new FakeProfileSession { IsRunning = false };
        var vm = Build(TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha")), session);
        Assert.True(vm.CanSwitchProfile);

        session.IsRunning = true; // the timer flipped it

        Assert.False(vm.CanSwitchProfile);
        Assert.Contains("Darktide is running", vm.ProfileSwitchTooltip);
    }

    // ---- manage dialog coordination ----------------------------------------

    [Fact]
    public async Task ManageProfiles_opens_the_dialog_once()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService();
        var vm = Build(profiles, session, dialogs);

        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ManageProfilesCalls);
    }

    [Fact]
    public async Task ManageProfiles_refreshes_the_profile_list_after_close()
    {
        // The dialog (simulated) creates a profile during its session; the shell
        // reloads its list snapshot when the dialog closes.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService
        {
            OnManageProfiles = () => profiles.CreateProfile("Bravo"),
        };
        var vm = Build(profiles, session, dialogs);

        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Contains(vm.Profiles, p => p.Name == "Bravo");
    }

    [Fact]
    public async Task ManageProfiles_re_syncs_selection_to_the_session_active_after_close()
    {
        // The dialog applies active changes live through the session; on close the
        // shell follows the session's authoritative active id.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var dialogs = new FakeDialogService
        {
            OnManageProfiles = () => session.RequestActive(b.Id),
        };
        var vm = Build(profiles, session, dialogs);

        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Equal(b.Id, session.ActiveProfileId);
        Assert.Equal(b.Id, vm.SelectedProfile?.Id);
    }

    [Fact]
    public async Task ManageProfiles_deleting_the_active_clears_selection_and_blocks_launch()
    {
        // Belt-and-suspenders: delete-of-active (not running) clears the active id;
        // the shell's selection mirrors it to null, so CanLaunch (unchanged) keeps
        // Launch blocked because no profile is selected.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = a.Id,
            IsRunning = false,
        };
        var dialogs = new FakeDialogService
        {
            ConfirmResult = true,
            OnManageProfiles = () =>
            {
                // Simulate the dialog deleting the active profile: profile gone,
                // session reconciles (clears the active id, per the fix).
                profiles.DeleteProfile(a.Id);
                session.ReconcileActive();
            },
        };
        var vm = Build(profiles, session, dialogs);
        Assert.NotNull(vm.SelectedProfile);

        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedProfile);                  // active cleared
        Assert.False(vm.LaunchCommand.CanExecute(null)); // Launch blocked (no selection)
    }

    // ---- CanLaunch positive case (long-deferred) --------------------------

    [Fact]
    public void CanLaunch_is_true_when_a_profile_is_selected_and_the_game_is_not_running()
    {
        // The long-deferred CanLaunch positive-case test: Launch lights up
        // once a profile is selected and the game is stopped.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var vm = Build(TestDoubles.Profiles(a), session);

        Assert.True(vm.LaunchCommand.CanExecute(null));
    }

    [Fact]
    public void CanLaunch_is_false_when_no_profile_is_selected()
    {
        var vm = Build(TestDoubles.Profiles());

        Assert.False(vm.LaunchCommand.CanExecute(null));
    }

    [Fact]
    public void CanLaunch_is_false_when_the_game_is_running()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = true };
        var vm = Build(TestDoubles.Profiles(a), session);

        Assert.False(vm.LaunchCommand.CanExecute(null));
    }

    // ---- launch wiring (Track C) ------------------------------------------

    [Fact]
    public async Task Launch_calls_the_launch_service_with_the_active_profile_id()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var launch = new FakeLaunchService();
        var vm = Build(TestDoubles.Profiles(a),
            new FakeProfileSession { ActiveProfileId = a.Id }, launch: launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { a.Id }, launch.LaunchCalls);
    }

    [Fact]
    public async Task Launch_Launched_refreshes_running_state_immediately()
    {
        // On a successful launch: the session's Refresh is called so the
        // running indicator + CanLaunch react immediately (not on the next
        // poll). Successful launch surfaces no status note or other
        // confirmation; the running indicator is the durable signal.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var launch = new FakeLaunchService(); // default: Launched
        var vm = Build(TestDoubles.Profiles(a), session, launch: launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.Equal(1, session.RefreshCalls); // immediate refresh, not deferred to the poll
    }

    [Fact]
    public async Task Launch_Launched_running_state_refresh_triggers_CanLaunch_re_eval()
    {
        // The session's Refresh flips IsRunning to true (the game just started).
        // The shell mirrors it -> CanLaunch flips to false (the running indicator
        // + launch-availability react at once).
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var session = new FakeProfileSession
        {
            ActiveProfileId = a.Id,
            IsRunning = false,
        };
        // Wire the callback AFTER construction so the lambda can reference the
        // built session (the game-start simulation flips IsRunning on Refresh).
        session.OnRefresh = () => session.IsRunning = true;
        var launch = new FakeLaunchService(); // Launched
        var vm = Build(TestDoubles.Profiles(a), session, launch: launch);
        Assert.True(vm.LaunchCommand.CanExecute(null));

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.True(vm.IsGameRunning);
        Assert.False(vm.LaunchCommand.CanExecute(null)); // blocked now that it's running
    }

    [Fact]
    public async Task Launch_DiscoveryIncomplete_opens_the_escape_hatch_with_the_missing_fields()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var dialogs = new FakeDialogService();
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(
                LaunchStatus.DiscoveryIncomplete,
                "missing",
                new[] { "ProtonBinaryPath", "CompatdataPath" }),
        };
        var vm = Build(TestDoubles.Profiles(a),
            new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs, launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.Single(dialogs.EscapeHatchCalls);
        Assert.Equal(
            new[] { "ProtonBinaryPath", "CompatdataPath" },
            dialogs.EscapeHatchCalls[0]);
        // No auto-retry: the shell did not call Launch again.
        Assert.Single(launch.LaunchCalls);
    }

    [Fact]
    public async Task Launch_DiscoveryIncomplete_does_not_retry_when_the_user_submits()
    {
        // Even when the user submits (EscapeHatchResult=true), the shell does
        // not re-launch; the user clicks Launch again. A loop here would trap
        // the user if they could not get the paths right.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var dialogs = new FakeDialogService { EscapeHatchResult = true };
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(
                LaunchStatus.DiscoveryIncomplete, "missing", new[] { "SteamInstallPath" }),
        };
        var vm = Build(TestDoubles.Profiles(a),
            new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs, launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.Single(launch.LaunchCalls); // no retry
    }

    [Fact]
    public async Task Launch_Error_opens_an_alert_with_the_result_message()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var dialogs = new FakeDialogService();
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(LaunchStatus.Error, "boom", Array.Empty<string>()),
        };
        var vm = Build(TestDoubles.Profiles(a),
            new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs, launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.Single(dialogs.AlertCalls);
        Assert.Equal("boom", dialogs.AlertCalls[0].Message);
    }

    [Fact]
    public async Task Launch_StagingFailed_appends_the_exception_body_to_the_localized_framing()
    {
        // StagingFailed carries the raised exception's body on Message; the shell
        // composes the localized framing + hint (Strings.resx) followed by that
        // body, mirroring the Update/Import failure alerts. Both the framing and
        // the carried detail appear in the shown alert.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService();
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(LaunchStatus.StagingFailed, Message: "The parameter is incorrect", Array.Empty<string>()),
        };
        var vm = Build(TestDoubles.Profiles(a), session, dialogs, launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Launch_StagingFailedTitle"], dialogs.AlertCalls[0].Title);
        Assert.Contains(Localization["Launch_StagingFailedMessage"], dialogs.AlertCalls[0].Message);
        Assert.Contains("The parameter is incorrect", dialogs.AlertCalls[0].Message);
    }

    [Fact]
    public async Task Launch_with_no_selected_profile_is_a_no_op()
    {
        // Defense: even though CanLaunch gates this, a programmatic call with
        // no selection must not throw or call the service.
        var launch = new FakeLaunchService();
        var vm = Build(TestDoubles.Profiles(), launch: launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.Empty(launch.LaunchCalls);
    }

    // ---- OpenSettings -----------------------------------------------------

    [Fact]
    public async Task OpenSettings_opens_the_settings_dialog_once()
    {
        var dialogs = new FakeDialogService();
        var vm = Build(dialogs: dialogs);

        await vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.SettingsCalls);
    }

    [Fact]
    public async Task OpenSettings_reloads_the_mod_list_after_close()
    {
        // The shell reloads the mod list after the Settings dialog closes so the
        // rows reflect any out-of-band change rather than a stale snapshot.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var repo = new FakeModRepository();
        var modList = TestDoubles.BuildModList(profiles, session, repo);
        var dialogs = new FakeDialogService();
        var vm = new ShellViewModel(
            profiles,
            session,
            new FakeLaunchService(),
            dialogs,
            TestDoubles.BuildDmfPromptService(profiles, session),
            Localization,
            modList,
            new FakeAppUpdateService(),
            invokeOnUi: static action => action(),
            Logger,
            new FakeConfigLoader());

        // Initially the active profile has no mods.
        Assert.Empty(modList.Mods);

        // During settings, a mod lands in the profile out-of-band (simulating
        // the effect of an external change to what the join would produce).
        dialogs.OnSettings = () =>
        {
            var container = repo.CreateContainer(new UntrackedSource(), "NewMod");
            profiles.AddMod(a.Id, container.Id, ModVersionPolicy.Latest);
        };

        await vm.OpenSettingsCommand.ExecuteAsync(null);

        // The shell reloaded the mod list after Settings closed.
        Assert.Single(modList.Mods);
    }

    // ---- nxm handler status -----------------------------------------------

    [Fact]
    public void Constructor_reads_nxm_status_when_registrar_reports_registered()
    {
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };
        var vm = Build(nxmRegistrar: registrar);

        Assert.True(vm.IsNxmRegistered);
        Assert.Equal(Localization["Status_NxmRegistered"], vm.NxmHandlerStatusText);
        Assert.Equal(Localization["Status_NxmRegisteredTooltip"], vm.NxmHandlerStatusTooltip);
        Assert.Equal(1, registrar.IsRegisteredCalls);
    }

    [Fact]
    public void Constructor_reads_nxm_status_when_registrar_reports_not_registered()
    {
        var registrar = new FakeNxmHandlerRegistrar { Registered = false };
        var vm = Build(nxmRegistrar: registrar);

        Assert.False(vm.IsNxmRegistered);
        Assert.Equal(Localization["Status_NxmNotRegistered"], vm.NxmHandlerStatusText);
    }

    [Fact]
    public void Constructor_shows_unavailable_when_no_registrar()
    {
        // No registrar (unsupported platform): the status is unavailable.
        var vm = Build(nxmRegistrar: null);

        Assert.Null(vm.IsNxmRegistered);
        Assert.Equal(Localization["Status_NxmUnavailable"], vm.NxmHandlerStatusText);
    }

    [Fact]
    public async Task OpenIntegrations_refreshes_nxm_status_after_close()
    {
        // The user may toggle the nxm handler inside the Integrations dialog;
        // the shell re-reads the OS state on close so the status strip stays
        // accurate. The registrar's state flips during the dialog (simulated via
        // OnIntegrations) and the shell picks it up.
        var registrar = new FakeNxmHandlerRegistrar { Registered = false };
        var dialogs = new FakeDialogService
        {
            OnIntegrations = () => registrar.Registered = true,
        };
        var vm = Build(dialogs: dialogs, nxmRegistrar: registrar);
        Assert.False(vm.IsNxmRegistered!.Value);

        await vm.OpenIntegrationsCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.IntegrationsCalls);
        Assert.True(vm.IsNxmRegistered);
        Assert.Equal(Localization["Status_NxmRegistered"], vm.NxmHandlerStatusText);
    }

    // ---- "changes pending" status-strip signal ------------------------------

    [Fact]
    public void Status_dot_is_grey_and_text_says_not_running_when_the_game_is_stopped()
    {
        var vm = Build(TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha")),
            new FakeProfileSession { IsRunning = false });

        Assert.True(vm.ShowNotRunningDot);
        Assert.False(vm.ShowRunningCleanDot);
        Assert.False(vm.ShowRunningDirtyDot);
        Assert.Equal(Localization["Status_GameNotRunning"], vm.GameRunningText);
    }

    [Fact]
    public void Status_dot_is_green_and_text_says_running_when_running_and_in_sync()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var vm = Build(TestDoubles.Profiles(a),
            new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = true, HasPendingChanges = false });

        Assert.False(vm.ShowNotRunningDot);
        Assert.True(vm.ShowRunningCleanDot);
        Assert.False(vm.ShowRunningDirtyDot);
        Assert.Equal(Localization["Status_GameRunning"], vm.GameRunningText);
    }

    [Fact]
    public void Status_dot_is_yellow_and_text_says_changes_pending_when_running_and_dirty()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var vm = Build(TestDoubles.Profiles(a),
            new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = true, HasPendingChanges = true });

        Assert.False(vm.ShowNotRunningDot);
        Assert.False(vm.ShowRunningCleanDot);
        Assert.True(vm.ShowRunningDirtyDot);
        Assert.Equal(Localization["Status_GameRunningChangesPending"], vm.GameRunningText);
    }

    [Fact]
    public void Status_dot_reacts_when_the_session_pending_flag_flips_live()
    {
        // The shell mirrors the session's HasPendingChanges so a mod-list edit
        // (which sets the session flag) flips the dot + text without a restart.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = true };
        var vm = Build(TestDoubles.Profiles(a), session);
        Assert.True(vm.ShowRunningCleanDot);

        session.HasPendingChanges = true;

        Assert.True(vm.HasPendingStagedChanges);
        Assert.True(vm.ShowRunningDirtyDot);
        Assert.False(vm.ShowRunningCleanDot);
    }

    [Fact]
    public async Task Launch_Launched_clears_pending_changes_after_the_successful_stage()
    {
        // A successful launch re-staged the active profile's mod tree, so any
        // prior pending edits are now reflected: the flag clears (the dirty
        // indicator drops).
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false, HasPendingChanges = true };
        var launch = new FakeLaunchService(); // default: Launched
        var vm = Build(TestDoubles.Profiles(a), session, launch: launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.False(session.HasPendingChanges);
        Assert.False(vm.HasPendingStagedChanges);
    }

    [Fact]
    public async Task Launch_DiscoveryIncomplete_does_not_clear_pending_changes()
    {
        // Nothing was staged on a DiscoveryIncomplete launch, so the pending flag
        // is preserved (the edits still apply at the next successful stage).
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, HasPendingChanges = true };
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(LaunchStatus.DiscoveryIncomplete, "missing", new[] { "SteamInstallPath" }),
        };
        var vm = Build(TestDoubles.Profiles(a), session, launch: launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public async Task Launch_StagingFailed_does_not_clear_pending_changes()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, HasPendingChanges = true };
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(LaunchStatus.StagingFailed, "staging blew up", Array.Empty<string>()),
        };
        var vm = Build(TestDoubles.Profiles(a), session, launch: launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.True(session.HasPendingChanges);
    }

    [Fact]
    public async Task Launch_Error_does_not_clear_pending_changes()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, HasPendingChanges = true };
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(LaunchStatus.Error, "boom", Array.Empty<string>()),
        };
        var vm = Build(TestDoubles.Profiles(a), session, launch: launch);

        await vm.LaunchCommand.ExecuteAsync(null);

        Assert.True(session.HasPendingChanges);
    }
}
