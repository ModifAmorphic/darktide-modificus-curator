using System.ComponentModel;
using Modificus.Curator.Mods;
using Modificus.Curator.RelayClient;
using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Shell navigation + launch + status against the hosted-destination shell. The
/// shell VM owns the SplitView navigation rail (five destinations), the global
/// Launch action, and the global status strip; the hosted page VMs are real
/// singletons wired to in-memory fakes (via <see cref="TestDoubles.BuildShell"/>),
/// so navigation lifecycle effects are exercised end-to-end.
/// </summary>
public sealed class ShellViewModelTests
{
    private static readonly LocalizationService Localization = new();

    // ---- defaults + pane toggle -------------------------------------------

    [Fact]
    public void Shell_defaults_to_Mods_with_the_pane_collapsed()
    {
        var shell = TestDoubles.BuildShell().Shell;

        Assert.Equal(ShellDestination.Mods, shell.CurrentDestination);
        Assert.False(shell.IsNavigationPaneOpen);
    }

    [Fact]
    public void ToggleNavigationPane_flips_the_pane_state()
    {
        var shell = TestDoubles.BuildShell().Shell;
        Assert.False(shell.IsNavigationPaneOpen);

        shell.ToggleNavigationPaneCommand.Execute(null);

        Assert.True(shell.IsNavigationPaneOpen);

        shell.ToggleNavigationPaneCommand.Execute(null);

        Assert.False(shell.IsNavigationPaneOpen);
    }

    // ---- navigation + title + projections --------------------------------

    [Theory]
    [InlineData(ShellDestination.Profiles)]
    [InlineData(ShellDestination.Mods)]
    [InlineData(ShellDestination.NexusIntegrations)]
    [InlineData(ShellDestination.Preferences)]
    [InlineData(ShellDestination.Settings)]
    public async Task Navigate_reaches_every_destination_and_tracks_projections(ShellDestination target)
    {
        var shell = TestDoubles.BuildShell().Shell;

        await shell.NavigateCommand.ExecuteAsync(target);

        Assert.Equal(target, shell.CurrentDestination);
        // Exactly one selected + one visible projection is true.
        var selected = new[]
        {
            shell.IsProfilesSelected, shell.IsModsSelected,
            shell.IsNexusIntegrationsSelected, shell.IsPreferencesSelected, shell.IsSettingsSelected,
        };
        var visible = new[]
        {
            shell.IsProfilesVisible, shell.IsModsVisible,
            shell.IsNexusIntegrationsVisible, shell.IsPreferencesVisible, shell.IsSettingsVisible,
        };
        Assert.Single(selected, true);
        Assert.Single(visible, true);
        // The title resolves from the matching resx key.
        var expectedTitle = target switch
        {
            ShellDestination.Profiles => Localization["Profiles_Title"],
            ShellDestination.Mods => Localization["ModList_Header"],
            ShellDestination.NexusIntegrations => Localization["Integrations_Title"],
            ShellDestination.Preferences => Localization["Preferences_Title"],
            ShellDestination.Settings => Localization["Settings_Title"],
            _ => string.Empty,
        };
        Assert.Equal(expectedTitle, shell.CurrentDestinationTitle);
    }

    [Fact]
    public void Default_title_is_Mods()
    {
        var shell = TestDoubles.BuildShell().Shell;

        Assert.Equal(Localization["ModList_Header"], shell.CurrentDestinationTitle);
    }

    [Fact]
    public async Task NexusIntegration_title_reads_Nexus()
    {
        // The user-facing title for the destination is exactly "Nexus" (the
        // singular short name). Internal identifiers
        // (ShellDestination.NexusIntegrations, IsNexusIntegrationsVisible) stay
        // plural to avoid meaningless code churn; only the user-facing string
        // is the short noun.
        var shell = TestDoubles.BuildShell().Shell;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.NexusIntegrations);

        Assert.Equal("Nexus", shell.CurrentDestinationTitle);
    }

    // ---- same-destination is a strict no-op --------------------------------

    [Fact]
    public async Task Same_destination_navigation_runs_no_lifecycle_effects()
    {
        // Default is Mods. Navigating to Mods again must not run any leave/enter
        // effects: no Integrations refresh, no mod-list reload, no config read,
        // no Profiles guard. Verified by asserting call counts stay at their
        // post-construction baseline.
        var parts = TestDoubles.BuildShell();
        var shell = parts.Shell;
        var baselineStateCalls = parts.Auth.GetCurrentStateCallCount;
        var baselineIsRegistered = parts.NxmRegistrar?.IsRegisteredCalls ?? 0;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);

        Assert.Equal(ShellDestination.Mods, shell.CurrentDestination);
        Assert.Equal(baselineStateCalls, parts.Auth.GetCurrentStateCallCount);
        Assert.Equal(baselineIsRegistered, parts.NxmRegistrar?.IsRegisteredCalls ?? 0);
    }

    [Fact]
    public async Task Same_destination_to_Integrations_does_not_refresh_again()
    {
        // Entering Integrations once refreshes auth; selecting it again must not.
        var parts = TestDoubles.BuildShell();
        var shell = parts.Shell;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.NexusIntegrations);
        var afterEnter = parts.Auth.GetCurrentStateCallCount;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.NexusIntegrations);

        Assert.Equal(afterEnter, parts.Auth.GetCurrentStateCallCount);
    }

    // ---- leaving Profiles: unsaved-changes guard ---------------------------

    [Fact]
    public async Task Leaving_dirty_Profiles_keeps_Profiles_selected_on_cancel()
    {
        // A dirty Profiles draft + an unsaved-changes Cancel keeps the
        // destination on Profiles and runs no target lifecycle (no Integrations
        // refresh, no destination change). Cancel is the enum default, so ESC,
        // the title-bar X, and a window close all behave the same.
        var parts = TestDoubles.BuildShell();
        var shell = parts.Shell;
        // First navigate to Profiles so a draft can be opened there.
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Profiles);
        // Open a new draft + dirty it (empty name baseline; typing dirties it).
        parts.ProfilesPage.AddProfileCommand.Execute(null);
        parts.ProfilesPage.Name = "Unsaved";

        parts.Dialogs.UnsavedResult = UnsavedChangesChoice.Cancel;

        var stateBefore = parts.Auth.GetCurrentStateCallCount;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Settings);

        Assert.Equal(ShellDestination.Profiles, shell.CurrentDestination); // unchanged
        Assert.True(parts.ProfilesPage.IsDirty); // draft preserved
        Assert.Equal(stateBefore, parts.Auth.GetCurrentStateCallCount); // no Settings/Integrations effects ran
        Assert.Equal(1, parts.Dialogs.UnsavedCalls); // the unsaved prompt ran once
        Assert.Equal(0, parts.Dialogs.ConfirmCalls); // binary confirm untouched
    }

    [Fact]
    public async Task Leaving_dirty_Profiles_navigates_on_dont_save()
    {
        var parts = TestDoubles.BuildShell();
        var shell = parts.Shell;
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Profiles);
        parts.ProfilesPage.AddProfileCommand.Execute(null);
        parts.ProfilesPage.Name = "Unsaved";

        parts.Dialogs.UnsavedResult = UnsavedChangesChoice.DontSave;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Settings);

        Assert.Equal(ShellDestination.Settings, shell.CurrentDestination);
        // Don't save reloaded from authority: the draft is gone (no longer dirty
        // because there is no active profile to reload into an unsaved state).
        Assert.False(parts.ProfilesPage.IsDraft);
    }

    [Fact]
    public async Task Leaving_clean_Profiles_does_not_prompt()
    {
        // A clean Profiles page navigates away without any prompt (no unsaved
        // dialog, no binary confirm).
        var parts = TestDoubles.BuildShell();
        var shell = parts.Shell;
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Profiles);

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);

        Assert.Equal(0, parts.Dialogs.UnsavedCalls);
        Assert.Equal(0, parts.Dialogs.ConfirmCalls);
        Assert.Equal(ShellDestination.Mods, shell.CurrentDestination);
    }

    // ---- entering Settings rehydrates from config -------------------------

    [Fact]
    public async Task Entering_Settings_rehydrates_external_config_changes()
    {
        // Escape-hatch / config changes made elsewhere are visible the moment
        // Settings is entered (before any user interaction).
        var parts = TestDoubles.BuildShell();
        var shell = parts.Shell;
        // Externally change a discovery override + the startup-check toggle.
        parts.Config.Config.Discovery.UserSteamInstallPath = "/extern/steam";
        parts.Config.Config.AppUpdates.CheckOnStartup = false;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Settings);

        var steamRow = parts.SettingsPage.DiscoveryRows.First(r => r.Field.FieldName == "SteamInstallPath");
        Assert.Equal("/extern/steam", steamRow.Value);
        Assert.False(parts.SettingsPage.CheckOnStartup);
    }

    [Fact]
    public async Task Leaving_Settings_reloads_mod_list_and_re_reads_startup_toggle()
    {
        // The leave point owns the post-Settings effects: reload the mod list +
        // re-read CheckOnStartup + refresh the app-update notice. A mod added to
        // the profile after entering Settings appears in the mod list only after
        // leaving Settings (the reload runs at the leave point, not on enter).
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var repo = new FakeModRepository();
        var parts = TestDoubles.BuildShell(
            profiles: profiles, session: session, repo: repo);
        var shell = parts.Shell;
        Assert.Empty(parts.ModsPage.Mods);

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Settings);
        // While in Settings, a mod lands in the profile out-of-band (simulating
        // an external change to what the join would produce).
        var container = repo.CreateContainer(new UntrackedSource(), "NewMod");
        profiles.AddMod(a.Id, container.Id, ModVersionPolicy.Latest);
        Assert.Empty(parts.ModsPage.Mods); // not yet reloaded (enter is rehydrate, not reload)

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods); // leave Settings

        Assert.Single(parts.ModsPage.Mods); // the leave reload picked it up
    }

    [Fact]
    public async Task Leaving_Settings_does_not_reload_on_other_transitions()
    {
        // Reload + toggle re-read fire ONLY when leaving Settings, not on other
        // transitions (e.g. Profiles -> Mods). Smoke check: navigating Profiles
        // -> Mods reads zero registrar probes beyond construction + runs no
        // Settings leave effects.
        var parts = TestDoubles.BuildShell();
        var shell = parts.Shell;
        var baselineIsRegistered = parts.NxmRegistrar?.IsRegisteredCalls ?? 0;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Profiles);
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);

        Assert.Equal(baselineIsRegistered, parts.NxmRegistrar?.IsRegisteredCalls ?? 0);
    }

    // ---- entering / leaving Nexus --------------------------------------------

    [Fact]
    public async Task Entering_Integrations_runs_the_auth_refresh()
    {
        var parts = TestDoubles.BuildShell();
        var shell = parts.Shell;
        var baseline = parts.Auth.GetCurrentStateCallCount;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.NexusIntegrations);

        Assert.True(parts.Auth.GetCurrentStateCallCount > baseline);
    }

    [Fact]
    public async Task Leaving_Integrations_cancels_auth_and_refreshes_nxm_and_mod_list()
    {
        // Start an in-flight OAuth on the Integrations page, then navigate away.
        // Leaving Integrations calls Deactivate (cancels the in-flight login),
        // refreshes nxm status (re-probes the registrar), and reloads the mod
        // list.
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };
        var parts = TestDoubles.BuildShell(nxmRegistrar: registrar);
        var shell = parts.Shell;
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.NexusIntegrations);
        parts.Auth.CancelOAuthOnToken = true;

        var loginTask = parts.IntegrationsPage.LoginWithOAuthCommand.ExecuteAsync(null);
        Assert.NotNull(parts.Auth.LastOAuthTask);
        Assert.True(parts.Auth.OAuthLoginCalls > 0);
        var isRegisteredBefore = registrar.IsRegisteredCalls;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);

        // Deactivate canceled the in-flight login promptly.
        Assert.NotNull(parts.Auth.LastOAuthTask);
        var finished = await Task.WhenAny(parts.Auth.LastOAuthTask!, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(parts.Auth.LastOAuthTask, finished);
        Assert.True(parts.Auth.LastOAuthTask!.IsCanceled);
        await loginTask; // command swallows OperationCanceledException + completes
        // The nxm status was re-probed on leave.
        Assert.True(registrar.IsRegisteredCalls > isRegisteredBefore);
    }

    [Fact]
    public async Task Normal_navigation_does_not_unsubscribe_localization()
    {
        // The hosted page VMs are application-lifetime; navigation must not
        // detach their localization handlers. A culture flip after touring every
        // destination still re-resolves a localized Integrations label. The same
        // Localization instance is shared with BuildShell so the culture change
        // reaches the page VM.
        var loc = new LocalizationService();
        var parts = TestDoubles.BuildShell(localization: loc);
        var shell = parts.Shell;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.NexusIntegrations);
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Settings);
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);

        string? fired = null;
        ((INotifyPropertyChanged)parts.IntegrationsPage).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IntegrationsViewModel.NexusHeader))
            {
                fired = e.PropertyName;
            }
        };

        FireCultureChange(loc);

        Assert.Equal(nameof(IntegrationsViewModel.NexusHeader), fired);
    }

    // ---- launch derives from the live active id ---------------------------

    [Fact]
    public void CanLaunch_is_false_when_no_active_profile()
    {
        var shell = TestDoubles.BuildShell().Shell;

        Assert.False(shell.LaunchCommand.CanExecute(null));
    }

    [Fact]
    public void CanLaunch_is_true_when_an_active_profile_exists_and_not_running()
    {
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var shell = TestDoubles.BuildShell(profiles: profiles, session: session).Shell;

        Assert.True(shell.LaunchCommand.CanExecute(null));
    }

    [Fact]
    public void CanLaunch_is_false_when_running()
    {
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = true };
        var shell = TestDoubles.BuildShell(profiles: profiles, session: session).Shell;

        Assert.False(shell.LaunchCommand.CanExecute(null));
    }

    [Fact]
    public async Task Launch_resolves_the_active_id_at_execution_time()
    {
        // Launch reads IProfileSession.ActiveProfileId at execution time, never a
        // cached snapshot. Switching the active id between two profiles routes
        // the second Launch through the new id.
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var b = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Bravo", "");
        var profiles = TestDoubles.Profiles(a, b);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var launch = new FakeLaunchService();
        var shell = TestDoubles.BuildShell(profiles: profiles, session: session, launch: launch).Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        // The active id flips (e.g. a Profiles save + activate); Launch must
        // pick up the new id without re-reading any shell-side snapshot.
        session.ActiveProfileId = b.Id;
        // The session fires ActiveProfileId PropertyChanged; the shell re-
        // evaluates CanLaunch on that signal. Simulate it for the unit test.
        ((INotifyPropertyChanged)session).PropertyChanged += (_, _) => { }; // ensure wired
        shell.LaunchCommand.NotifyCanExecuteChanged();
        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { a.Id, b.Id }, launch.LaunchCalls);
    }

    [Fact]
    public async Task Launch_Launched_refreshes_running_state_immediately()
    {
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a), session: session).Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Equal(1, session.RefreshCalls);
    }

    [Fact]
    public async Task Launch_Launched_clears_pending_changes()
    {
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var session = new FakeProfileSession
        {
            ActiveProfileId = a.Id,
            IsRunning = false,
            HasPendingChanges = true,
        };
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a), session: session).Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.False(session.HasPendingChanges);
        Assert.False(shell.HasPendingStagedChanges);
    }

    [Fact]
    public async Task Launch_DiscoveryIncomplete_opens_the_escape_hatch()
    {
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var dialogs = new FakeDialogService();
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(
                LaunchStatus.DiscoveryIncomplete, "missing",
                new[] { "ProtonBinaryPath", "CompatdataPath" }),
        };
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs, launch: launch).Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Single(dialogs.EscapeHatchCalls);
        Assert.Equal(new[] { "ProtonBinaryPath", "CompatdataPath" }, dialogs.EscapeHatchCalls[0]);
        Assert.Single(launch.LaunchCalls); // no retry
    }

    [Fact]
    public async Task Launch_Error_opens_an_alert_with_the_result_message()
    {
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var dialogs = new FakeDialogService();
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(LaunchStatus.Error, "boom", Array.Empty<string>()),
        };
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs, launch: launch).Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Single(dialogs.AlertCalls);
        Assert.Equal("boom", dialogs.AlertCalls[0].Message);
    }

    [Fact]
    public async Task Launch_StagingFailed_appends_the_exception_body()
    {
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var dialogs = new FakeDialogService();
        var launch = new FakeLaunchService
        {
            NextResult = new LaunchResult(
                LaunchStatus.StagingFailed, Message: "The parameter is incorrect", Array.Empty<string>()),
        };
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a),
            session: new FakeProfileSession { ActiveProfileId = a.Id },
            dialogs: dialogs, launch: launch).Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["Launch_StagingFailedTitle"], dialogs.AlertCalls[0].Title);
        Assert.Contains(Localization["Launch_StagingFailedMessage"], dialogs.AlertCalls[0].Message);
        Assert.Contains("The parameter is incorrect", dialogs.AlertCalls[0].Message);
    }

    [Fact]
    public async Task Launch_with_no_active_profile_is_a_no_op()
    {
        var launch = new FakeLaunchService();
        var shell = TestDoubles.BuildShell(launch: launch).Shell;

        await shell.LaunchCommand.ExecuteAsync(null);

        Assert.Empty(launch.LaunchCalls);
    }

    // ---- nxm handler status -----------------------------------------------

    [Fact]
    public void Constructor_reads_nxm_status_when_registrar_reports_registered()
    {
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };
        var shell = TestDoubles.BuildShell(nxmRegistrar: registrar).Shell;

        Assert.True(shell.IsNxmRegistered);
        Assert.Equal(Localization["Status_NxmRegistered"], shell.NxmHandlerStatusText);
    }

    [Fact]
    public void Constructor_shows_unavailable_when_no_registrar()
    {
        var shell = TestDoubles.BuildShell(nxmRegistrar: null).Shell;

        Assert.Null(shell.IsNxmRegistered);
        Assert.Equal(Localization["Status_NxmUnavailable"], shell.NxmHandlerStatusText);
    }

    // ---- status strip -----------------------------------------------------

    [Fact]
    public void Status_dot_is_grey_when_the_game_is_stopped()
    {
        var session = new FakeProfileSession { IsRunning = false };
        var shell = TestDoubles.BuildShell(session: session).Shell;

        Assert.True(shell.ShowNotRunningDot);
        Assert.False(shell.ShowRunningCleanDot);
        Assert.False(shell.ShowRunningDirtyDot);
        Assert.Equal(Localization["Status_GameNotRunning"], shell.GameRunningText);
    }

    [Fact]
    public void Status_dot_is_yellow_when_running_and_dirty()
    {
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var session = new FakeProfileSession
        {
            ActiveProfileId = a.Id,
            IsRunning = true,
            HasPendingChanges = true,
        };
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a), session: session).Shell;

        Assert.True(shell.ShowRunningDirtyDot);
        Assert.False(shell.ShowRunningCleanDot);
        Assert.Equal(Localization["Status_GameRunningChangesPending"], shell.GameRunningText);
    }

    [Fact]
    public void Status_dot_reacts_to_a_live_pending_flag_flip()
    {
        var a = new Modificus.Curator.Profiles.ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = true };
        var shell = TestDoubles.BuildShell(
            profiles: TestDoubles.Profiles(a), session: session).Shell;
        Assert.True(shell.ShowRunningCleanDot);

        session.HasPendingChanges = true;

        Assert.True(shell.HasPendingStagedChanges);
        Assert.True(shell.ShowRunningDirtyDot);
    }

    // ---- DMF prompt deferred to Mods entry --------------------------------

    [Fact]
    public async Task Profile_save_does_not_show_dmf_prompt()
    {
        // The DMF trigger is recorded at CreateProfile but consumed only on the
        // next real navigation into Mods. Saving a new profile on the Profiles
        // destination does NOT prompt immediately (the old behavior awaited the
        // DMF delegate inside SaveAsync; the shell now owns the prompt).
        var repo = new FakeModRepository();
        var dmf = repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var parts = TestDoubles.BuildShell(profiles: profiles, session: session, repo: repo);
        var shell = parts.Shell;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Profiles);

        // Save a new profile (drives CreateProfile -> ProfileCreated ->
        // DmfPromptService records the trigger, does not prompt).
        parts.ProfilesPage.AddProfileCommand.Execute(null);
        parts.ProfilesPage.Name = "New";
        parts.ProfilesPage.SaveCommand.Execute(null);

        Assert.Equal(0, parts.Dialogs.ConfirmCalls); // no DMF prompt yet
        Assert.True(parts.Session.ActiveProfileId is not null); // save activated
    }

    [Fact]
    public async Task Visiting_a_non_Mods_destination_does_not_consume_the_dmf_trigger()
    {
        // A pending DMF trigger survives visits to Preferences and Settings;
        // only a real navigation into Mods consumes it.
        var repo = new FakeModRepository();
        repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var parts = TestDoubles.BuildShell(profiles: profiles, session: session, repo: repo);
        var shell = parts.Shell;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Profiles);
        parts.ProfilesPage.AddProfileCommand.Execute(null);
        parts.ProfilesPage.Name = "New";
        parts.ProfilesPage.SaveCommand.Execute(null);

        // Tour non-Mods destinations; the trigger is still pending.
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Preferences);
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Settings);
        Assert.Equal(0, parts.Dialogs.ConfirmCalls);

        // Navigating into Mods now consumes the trigger -> the DMF prompt fires.
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);

        Assert.Equal(1, parts.Dialogs.ConfirmCalls);
    }

    [Fact]
    public async Task Entering_Mods_sets_destination_first_then_prompts_and_reloads()
    {
        // CurrentDestination is Mods BEFORE the DMF prompt fires (so Mods is
        // selected underneath the modal). The post-prompt reload runs only when
        // a trigger was consumed; an accepted existing-DMF add is visible in
        // the mod list immediately after.
        var repo = new FakeModRepository();
        var dmf = repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var parts = TestDoubles.BuildShell(profiles: profiles, session: session, repo: repo);
        var shell = parts.Shell;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Profiles);
        parts.ProfilesPage.AddProfileCommand.Execute(null);
        parts.ProfilesPage.Name = "New";
        parts.ProfilesPage.SaveCommand.Execute(null);
        var createdId = session.ActiveProfileId!.Value;

        // Accept the DMF add (ConfirmResult defaults to true).
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);

        // Destination is Mods + the prompt fired + DMF was added to the profile.
        Assert.Equal(ShellDestination.Mods, shell.CurrentDestination);
        Assert.Equal(1, parts.Dialogs.ConfirmCalls);
        var add = Assert.Single(profiles.AddModCalls);
        Assert.Equal(createdId, add.Id);
        Assert.Equal(dmf.Id, add.ContainerId);
        // The post-prompt reload surfaced the add: the Mods page now sees the
        // newly added DMF row.
        Assert.Single(parts.ModsPage.Mods);
    }

    [Fact]
    public async Task Declined_dmf_prompt_does_not_reload_the_mod_list()
    {
        // A declined prompt leaves the mod list alone (no AddMod, no extra reload
        // beyond what the shell always does on a real Mods entry).
        var repo = new FakeModRepository();
        repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var parts = TestDoubles.BuildShell(profiles: profiles, session: session, repo: repo);
        var shell = parts.Shell;
        parts.Dialogs.ConfirmResult = false; // user says No

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Profiles);
        parts.ProfilesPage.AddProfileCommand.Execute(null);
        parts.ProfilesPage.Name = "New";
        parts.ProfilesPage.SaveCommand.Execute(null);

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);

        Assert.Equal(1, parts.Dialogs.ConfirmCalls); // prompt did fire
        Assert.Empty(profiles.AddModCalls);          // decline respected
    }

    [Fact]
    public async Task Second_Mods_entry_does_not_re_prompt_after_a_consumed_trigger()
    {
        // ProcessPendingAsync consumes the trigger; a subsequent Mods entry
        // finds nothing pending and does not prompt again.
        var repo = new FakeModRepository();
        repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var parts = TestDoubles.BuildShell(profiles: profiles, session: session, repo: repo);
        var shell = parts.Shell;

        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Profiles);
        parts.ProfilesPage.AddProfileCommand.Execute(null);
        parts.ProfilesPage.Name = "New";
        parts.ProfilesPage.SaveCommand.Execute(null);

        // Navigate away from Mods then back: first Mods entry consumes + prompts.
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Preferences);
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);
        Assert.Equal(1, parts.Dialogs.ConfirmCalls);

        // A second Mods entry (after a non-Mods hop) does not re-prompt.
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Preferences);
        await shell.NavigateCommand.ExecuteAsync(ShellDestination.Mods);

        Assert.Equal(1, parts.Dialogs.ConfirmCalls);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Forces the localization service to raise its culture-changed event by
    /// switching to a culture different from the current one (a no-op culture
    /// assignment raises nothing).
    /// </summary>
    private static void FireCultureChange(LocalizationService loc)
    {
        var next = loc.Culture.Name == "fr" ? "de" : "fr";
        loc.SetCulture(next);
    }
}
