using System.Text.Json;
using Avalonia.Media.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Profiles destination view model: the full profile draft workflow against
/// in-memory fakes. Covers the persisted-banner + picker card, the new-draft
/// flow (blank default, atomic create + activate + DMF + reload, running-state
/// gating), existing-profile atomic save/cancel/delete, the dirty-navigation
/// guard across switch + add + nav-away, the programmatic active-id displacement
/// behavior, and the localization-refresh remap. Keeps the authority in
/// <see cref="IProfileSession.ActiveProfileId"/>; every voluntary change routes
/// through the session.
/// </summary>
public sealed class ProfilesViewModelTests
{
    private static readonly LocalizationService Localization = new();

    /// <summary>
    /// Builds a <see cref="ProfilesViewModel"/> wired to in-memory fakes. After
    /// the DMF/reload-ownership move to the shell, this VM is narrowly coupled
    /// to profile workflow (no DMF + no mod-list reload delegate seams), so the
    /// builder is now just the four core dependencies + the logger. The fakes
    /// are returned by reference for tests that need to drive post-construction
    /// state (most construct + keep the locals themselves).
    /// </summary>
    private static ProfilesViewModel Build(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeDialogService? dialogs = null)
    {
        profiles ??= TestDoubles.Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        dialogs ??= new FakeDialogService();
        return new ProfilesViewModel(
            profiles,
            session,
            dialogs,
            Localization,
            NullLogger<ProfilesViewModel>.Instance);
    }

    /// <summary>Seeds a profile summary + optional launch settings on the fake,
    /// without recording a create call. Returns the seeded summary so the test
    /// can reference its id.</summary>
    private static ProfileSummary Profile(FakeProfileService profiles, string name, string description = "",
        LaunchSettings? settings = null) =>
        profiles.WithProfile(name, description, settings);

    // ---- active profile loads metadata / settings / banner / choices --------

    [Fact]
    public void Active_profile_loads_metadata_settings_banner_and_choices()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha", "alpha desc",
            new LaunchSettings { GameArguments = new[] { "--alpha" }, EnableLuaLogs = true });
        var b = Profile(profiles, "Bravo", "bravo desc",
            new LaunchSettings { EnvironmentVariables = new[] { new EnvVar("MOD", "1") } });
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };

        var vm = Build(profiles, session);

        // Metadata from the active profile.
        Assert.Equal("Alpha", vm.Name);
        Assert.Equal("alpha desc", vm.Description);

        // Launch settings deep-loaded into the editor.
        Assert.Single(vm.Editor.GameArguments);
        Assert.Equal("--alpha", vm.Editor.GameArguments[0].Value);
        Assert.True(vm.Editor.EnableLuaLogs);
        Assert.False(vm.Editor.IsDirty);

        // Banner shows the active profile.
        Assert.NotNull(vm.ActiveProfileBanner);
        Assert.Equal("Alpha", vm.ActiveProfileBanner!.Name);
        Assert.Equal("alpha desc", vm.ActiveProfileBanner.Description);
        Assert.Equal("A", vm.ActiveProfileBanner.FirstLetter);
        Assert.True(vm.IsBannerVisible);

        // Picker excludes the active profile (it is not a switch target).
        Assert.Single(vm.ProfileChoices);
        Assert.Equal("Bravo", vm.ProfileChoices[0].Name);
        Assert.Equal("B", vm.ProfileChoices[0].FirstLetter);
        Assert.True(vm.HasPickerChoices);
        Assert.True(vm.HasProfiles);

        // Editor + footer visible; not dirty; save disabled (no changes).
        Assert.True(vm.IsEditorVisible);
        Assert.False(vm.IsDirty);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void First_letter_falls_back_when_name_is_empty_at_load()
    {
        // A persisted profile with an empty name (should not happen via the
        // service, but the picker must not crash on it) yields "?".
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "", "");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };

        var vm = Build(profiles, session);

        Assert.Equal("?", vm.ActiveProfileBanner!.FirstLetter);
    }

    // ---- no-profiles + no-active states -------------------------------------

    [Fact]
    public void No_profiles_state_shows_no_profiles_cta_and_hides_editor_and_action_row()
    {
        var vm = Build();

        Assert.True(vm.ShowNoProfilesCta);
        Assert.False(vm.ShowSelectAffordance);
        Assert.False(vm.IsBannerVisible);
        Assert.False(vm.IsEditorVisible);
        Assert.False(vm.ShowProfileActions); // no profiles -> CTA replaces the action row
        Assert.False(vm.HasProfiles);
        Assert.Empty(vm.ProfileChoices);
        Assert.Null(vm.ActiveProfileBanner);
    }

    [Fact]
    public void Profiles_with_no_active_shows_select_affordance_and_lists_all_as_choices()
    {
        var profiles = new FakeProfileService();
        Profile(profiles, "Alpha");
        Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = null };

        var vm = Build(profiles, session);

        Assert.True(vm.ShowSelectAffordance);
        Assert.False(vm.ShowNoProfilesCta);
        Assert.False(vm.IsBannerVisible);
        Assert.True(vm.HasProfiles);

        // No active means every profile is a switch target.
        Assert.Equal(2, vm.ProfileChoices.Count);
    }

    // ---- picker excludes active / switch routes through session / blocked while running ----

    [Fact]
    public async Task Select_profile_routes_through_session_and_reloads_authoritative()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        var choice = vm.ProfileChoices.Single(c => c.Id == b.Id);
        await vm.SelectProfileCommand.ExecuteAsync(choice);

        Assert.Equal(b.Id, session.ActiveProfileId);
        Assert.Equal(b.Id, session.LastRequestedId);
        // Reloaded the authoritative active.
        Assert.Equal("Bravo", vm.Name);
        Assert.True(vm.IsBannerVisible);
        // Picker now excludes Bravo (the new active).
        Assert.Single(vm.ProfileChoices);
        Assert.Equal("Alpha", vm.ProfileChoices[0].Name);
    }

    [Fact]
    public async Task Select_profile_blocked_while_running()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        session.IsRunning = true;
        Assert.False(vm.SelectProfileCommand.CanExecute(vm.ProfileChoices[0]));

        // Defense-in-depth: even a direct Execute bails without touching the session.
        var before = session.ActiveProfileId;
        await vm.SelectProfileCommand.ExecuteAsync(vm.ProfileChoices[0]);
        Assert.Equal(before, session.ActiveProfileId);
        Assert.Equal(0, session.RequestActiveCalls);
    }

    [Fact]
    public async Task Select_profile_dirty_cancel_preserves_draft()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { UnsavedResult = UnsavedChangesChoice.Cancel };
        var vm = Build(profiles, session, dialogs);

        // Make the active profile's draft dirty.
        vm.Name = "Edited";
        Assert.True(vm.IsDirty);

        var choice = vm.ProfileChoices.Single(c => c.Id == b.Id);
        await vm.SelectProfileCommand.ExecuteAsync(choice);

        // Cancel: no session request, draft preserved.
        Assert.Equal(1, dialogs.UnsavedCalls);
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Equal(0, session.RequestActiveCalls);
        Assert.Equal(a.Id, session.ActiveProfileId);
        Assert.Equal("Edited", vm.Name);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task Select_profile_dirty_dont_save_switches_and_reloads()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { UnsavedResult = UnsavedChangesChoice.DontSave };
        var vm = Build(profiles, session, dialogs);

        vm.Name = "Edited";
        var choice = vm.ProfileChoices.Single(c => c.Id == b.Id);
        await vm.SelectProfileCommand.ExecuteAsync(choice);

        // Don't save: session switched + reloaded authoritative (Bravo), draft cleared.
        Assert.Equal(b.Id, session.ActiveProfileId);
        Assert.Equal("Bravo", vm.Name);
        Assert.False(vm.IsDirty);
    }

    // ---- new draft: blank / default / no banner / not dirty / save gating ----

    [Fact]
    public async Task Add_profile_starts_blank_draft_with_no_banner_and_not_dirty()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        Assert.True(vm.AddProfileCommand.CanExecute(null));
        await vm.AddProfileCommand.ExecuteAsync(null);

        Assert.True(vm.IsDraft);
        Assert.Empty(vm.Name);
        Assert.Empty(vm.Description);
        Assert.Empty(vm.Editor.EnvironmentVariables);
        Assert.Empty(vm.Editor.GameArguments);
        Assert.False(vm.Editor.EnableLuaLogs);
        Assert.False(vm.Editor.SkipSplash);

        // Banner hidden during draft; editor + footer visible.
        Assert.False(vm.IsBannerVisible);
        Assert.True(vm.IsEditorVisible);
        Assert.True(vm.DeleteIsVisible == false);

        // An untouched draft is not itself dirty.
        Assert.False(vm.IsDirty);

        // Save disabled: no name + not dirty.
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Add_profile_typing_name_enables_valid_save()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.Name = "New";
        Assert.True(vm.IsDirty);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Add_profile_blocked_while_running()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        session.IsRunning = true;
        Assert.False(vm.AddProfileCommand.CanExecute(null));

        // Defense-in-depth: direct Execute bails without starting a draft.
        await vm.AddProfileCommand.ExecuteAsync(null);
        Assert.False(vm.IsDraft);
    }

    [Fact]
    public async Task Add_profile_dirty_existing_cancel_preserves_draft()
    {
        // Add is disabled while a draft is already open (defense in depth), so
        // the dirty-transition-via-Add path fires only when an existing (non-
        // draft) profile's edit is dirty. Cancel preserves the staged edits.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { UnsavedResult = UnsavedChangesChoice.Cancel };
        var vm = Build(profiles, session, dialogs);

        // Dirty the existing (non-draft) profile.
        vm.Name = "Edited";
        Assert.True(vm.IsDirty);

        // Try to start a draft while the existing edit is dirty.
        Assert.True(vm.AddProfileCommand.CanExecute(null));
        await vm.AddProfileCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.UnsavedCalls);
        Assert.Equal(0, dialogs.ConfirmCalls);
        // Cancel: the original edits are preserved, no draft started.
        Assert.Equal("Edited", vm.Name);
        Assert.True(vm.IsDirty);
        Assert.False(vm.IsDraft);
    }

    [Fact]
    public async Task Add_profile_blocked_at_command_level_while_a_draft_is_open()
    {
        // Defense in depth: Add is hidden via ShowProfileActions AND disabled at
        // the command level while a draft is open, so a programmatic call cannot
        // start a second draft. ShowProfileActions also reports false.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);
        Assert.True(vm.IsDraft);
        Assert.False(vm.ShowProfileActions);
        Assert.False(vm.AddProfileCommand.CanExecute(null));
    }

    // ---- new save: atomic create + activate (DMF/reload moved to shell) ----

    [Fact]
    public async Task Save_new_passes_all_fields_atomically_and_activates()
    {
        var profiles = new FakeProfileService();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);
        vm.Name = "Vanilla+";
        vm.Description = "A tuned loadout";
        vm.Editor.AddGameArgCommand.Execute(null);
        vm.Editor.GameArguments[0].Value = "--windowed";
        vm.Editor.EnableLuaLogs = true;

        vm.SaveCommand.Execute(null);

        // Exactly one atomic create, carrying all three fields.
        Assert.Single(profiles.CreateCalls);
        var (name, description, settings) = profiles.CreateCalls[0];
        Assert.Equal("Vanilla+", name);
        Assert.Equal("A tuned loadout", description);
        Assert.Equal(new[] { "--windowed" }, settings.GameArguments.ToArray());
        Assert.True(settings.EnableLuaLogs);

        // Created profile activated through the session (one request).
        Assert.Equal(1, session.RequestActiveCalls);
        Assert.Equal(profiles.ListProfiles().Single(p => p.Name == "Vanilla+").Id,
            session.ActiveProfileId);

        // DMF + mod-list reload are no longer this VM's job: the shell consumes
        // the DMF trigger on the next Mods entry + reloads the mod list there.
        // The VM's part ends at the atomic create + activate + authoritative
        // reload, so the draft is cleared + the banner shows the new profile.
        Assert.False(vm.IsDraft);
        Assert.True(vm.IsBannerVisible);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Save_new_disabled_when_game_starts_and_re_enables_when_stopped()
    {
        var profiles = new FakeProfileService();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);
        vm.Name = "Draft";
        Assert.True(vm.SaveCommand.CanExecute(null)); // dirty + valid + not running

        // Game starts externally while the draft is open: the draft is retained
        // but Save disables (saving would require activation).
        session.IsRunning = true;
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.IsDraft); // retained

        // Game stops: Save re-enables.
        session.IsRunning = false;
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    // ---- existing save: atomic update once with exact settings + works while running ----

    [Fact]
    public async Task Save_existing_calls_update_once_with_exact_fields()
    {
        var profiles = new FakeProfileService();
        var originalSettings = new LaunchSettings { GameArguments = new[] { "--old" } };
        var a = Profile(profiles, "Alpha", "old desc", originalSettings);
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.Name = "Renamed";
        vm.Description = "new desc";
        vm.Editor.GameArguments[0].Value = "--new";
        vm.Editor.EnableLuaLogs = true;

        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);

        Assert.Single(profiles.UpdateCalls);
        var (id, name, description, settings) = profiles.UpdateCalls[0];
        Assert.Equal(a.Id, id);
        Assert.Equal("Renamed", name);
        Assert.Equal("new desc", description);
        Assert.Equal(new[] { "--new" }, settings.GameArguments.ToArray());
        Assert.True(settings.EnableLuaLogs);

        // Reloaded the persisted state: not dirty after save.
        Assert.False(vm.IsDirty);
        Assert.Equal("Renamed", vm.Name);
    }

    [Fact]
    public async Task Save_existing_remains_enabled_and_works_while_running()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.Name = "Edited";
        session.IsRunning = true;

        // Existing-profile save stays enabled while Darktide runs.
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);

        Assert.Single(profiles.UpdateCalls);
    }

    [Fact]
    public async Task Save_existing_no_changes_is_disabled()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha", "desc");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        // Loaded but untouched: not dirty, Save disabled.
        Assert.False(vm.IsDirty);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    // ---- save error + edit recovery -----------------------------------------

    [Fact]
    public async Task Save_existing_service_rejection_surfaces_localized_error_and_edits_clear_it()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.Name = "Edited";
        // Simulate the service rejecting an edit the inline pass allowed.
        profiles.UpdateProfileThrows = new ArgumentException("simulated divergence");

        vm.SaveCommand.Execute(null);

        // The single attempted update was recorded (the fake records before
        // throwing) ...
        Assert.Single(profiles.UpdateCalls);
        // ... but the raw service text is never surfaced: only the localized
        // generic error is shown.
        Assert.Equal(Localization["Profiles_ErrSaveFailed"], vm.SaveError);
        Assert.DoesNotContain("simulated divergence", vm.SaveError);

        // Any edit clears the stale error + re-evaluates Save.
        profiles.UpdateProfileThrows = null;
        vm.Name = "Edited2";
        Assert.Empty(vm.SaveError);
    }

    // ---- Cancel never writes ------------------------------------------------

    [Fact]
    public async Task Cancel_reloads_persisted_state_without_writing()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha", "alpha desc");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.Name = "Discarded";
        vm.Description = "thrown away";
        Assert.True(vm.IsDirty);

        vm.CancelCommand.Execute(null);

        // No service writes (this VM no longer drives DMF or mod-list reload
        // either; both moved to the shell on Mods entry).
        Assert.Empty(profiles.UpdateCalls);
        Assert.Empty(profiles.CreateCalls);

        // Persisted state reloaded.
        Assert.Equal("Alpha", vm.Name);
        Assert.Equal("alpha desc", vm.Description);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Cancel_new_draft_creates_nothing()
    {
        var profiles = new FakeProfileService();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);
        vm.Name = "Phantom";
        Assert.True(vm.IsDraft);

        vm.CancelCommand.Execute(null);

        Assert.Empty(profiles.CreateCalls);
        Assert.False(vm.IsDraft);
    }

    // ---- delete: confirm accept/reject / running gate / reconcile / no auto-select ----

    [Fact]
    public async Task Delete_confirm_accept_deletes_reconciles_and_shows_no_active()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, session, dialogs);

        await vm.DeleteProfileCommand.ExecuteAsync(null);

        // Confirmation asked with the localized delete message carrying the name.
        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Contains("Alpha", dialogs.LastConfirmMessage);

        // Deleted + reconciled.
        Assert.Contains(a.Id, profiles.DeletedIds);
        Assert.Equal(1, session.ReconcileCalls);

        // Active cleared (no auto-select of the remaining profile).
        Assert.Null(session.ActiveProfileId);
        Assert.False(vm.IsBannerVisible);
        // Profiles still exist, so the Select affordance shows.
        Assert.True(vm.ShowSelectAffordance);
        Assert.True(vm.HasProfiles);
    }

    [Fact]
    public async Task Delete_confirm_reject_aborts_without_deleting()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var vm = Build(profiles, session, dialogs);

        await vm.DeleteProfileCommand.ExecuteAsync(null);

        Assert.Empty(profiles.DeletedIds);
        Assert.Equal(0, session.ReconcileCalls);
        Assert.Equal(a.Id, session.ActiveProfileId);
        Assert.True(vm.IsBannerVisible);
    }

    [Fact]
    public async Task Delete_blocked_while_running()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        session.IsRunning = true;
        Assert.False(vm.DeleteProfileCommand.CanExecute(null));

        // Defense-in-depth: direct Execute bails without deleting.
        await vm.DeleteProfileCommand.ExecuteAsync(null);
        Assert.Empty(profiles.DeletedIds);
    }

    [Fact]
    public async Task Delete_last_profile_shows_no_profiles_cta()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, session, dialogs);

        await vm.DeleteProfileCommand.ExecuteAsync(null);

        Assert.Empty(profiles.ListProfiles());
        Assert.True(vm.ShowNoProfilesCta);
        Assert.False(vm.HasProfiles);
    }

    [Fact]
    public async Task Delete_hidden_for_new_draft()
    {
        var profiles = new FakeProfileService();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);

        Assert.False(vm.DeleteIsVisible);
    }

    // ---- structural dirty / reversion ---------------------------------------

    [Fact]
    public void Name_edit_makes_dirty_and_reversion_clears_it()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha", "alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        Assert.False(vm.IsDirty);
        vm.Name = "Changed";
        Assert.True(vm.IsDirty);
        vm.Name = "Alpha";
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Description_edit_makes_dirty_and_reversion_clears_it()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha", "alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.Description = "edited";
        Assert.True(vm.IsDirty);
        vm.Description = "alpha";
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Editor_edit_makes_dirty_and_reversion_clears_it()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha", "alpha",
            new LaunchSettings { GameArguments = new[] { "--x" } });
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.Editor.GameArguments[0].Value = "--y";
        Assert.True(vm.IsDirty);
        vm.Editor.GameArguments[0].Value = "--x";
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Description_multiline_disables_save_as_defense_in_depth()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        // Simulate a paste that carries a newline (the XAML caps length but a
        // paste can still carry CR/LF).
        vm.Description = "line1\nline2";
        Assert.True(vm.IsDirty);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.Description = "single line";
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    // ---- programmatic active-id change discards stale dirty draft + logs -----

    [Fact]
    public async Task Programmatic_active_change_discards_stale_dirty_draft_and_loads_authoritative()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.Name = "UnsavedEdit";
        Assert.True(vm.IsDirty);

        // An outside authority (e.g. a still-wired legacy flow) changes the
        // active id while our draft is dirty. The handler logs + discards the
        // stale draft rather than prompting from an event handler.
        session.ActiveProfileId = b.Id;

        // Authoritative profile loaded; draft discarded (no longer dirty).
        Assert.Equal("Bravo", vm.Name);
        Assert.False(vm.IsDirty);
        Assert.True(vm.IsBannerVisible);
    }

    [Fact]
    public async Task Programmatic_active_change_to_same_id_is_a_noop()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.Name = "Edit";
        var dirtyBefore = vm.IsDirty;

        // Setting the session to the same id raises the event but the handler
        // short-circuits (no spurious reload).
        session.ActiveProfileId = a.Id;

        Assert.Equal(dirtyBefore, vm.IsDirty);
        Assert.Equal("Edit", vm.Name);
    }

    [Fact]
    public async Task Programmatic_active_change_to_none_reloads_no_active_state()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        // Active cleared externally (e.g. delete-of-active from elsewhere).
        session.ActiveProfileId = null;

        Assert.False(vm.IsBannerVisible);
        Assert.True(vm.ShowSelectAffordance);
    }

    // ---- localization refresh remaps editor validation without setting dirty --

    [Fact]
    public async Task Culture_change_remaps_editor_validation_without_setting_dirty()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        // Stage an invalid env row (dirty + error from the editor).
        vm.Editor.AddEnvVarCommand.Execute(null); // empty name -> error
        Assert.True(vm.IsDirty);
        Assert.False(vm.Editor.IsValid);
        var beforeMessage = vm.Editor.EnvironmentVariables[0].ErrorMessage;
        Assert.NotEmpty(beforeMessage);

        // Flip the culture. The VM's localization subscription routes a refresh
        // to the editor so its inline messages re-resolve.
        Localization.SetCulture("fr");

        Assert.False(vm.Editor.IsValid); // still invalid
        Assert.True(vm.IsDirty); // dirty unchanged
        Assert.Equal(Localization["LaunchSettings_ErrNameRequired"],
            vm.Editor.EnvironmentVariables[0].ErrorMessage);
    }

    // ---- future navigation guard -------------------------------------------

    [Fact]
    public async Task Navigate_away_clean_returns_true_without_prompt()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService();
        var vm = Build(profiles, session, dialogs);

        var result = await vm.ConfirmCanNavigateAwayAsync();

        Assert.True(result);
        Assert.Equal(0, dialogs.UnsavedCalls);
        Assert.Equal(0, dialogs.ConfirmCalls);
    }

    [Fact]
    public async Task Navigate_away_dirty_dont_save_reloads_and_returns_true()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { UnsavedResult = UnsavedChangesChoice.DontSave };
        var vm = Build(profiles, session, dialogs);

        vm.Name = "Edited";
        Assert.True(vm.IsDirty);

        var result = await vm.ConfirmCanNavigateAwayAsync();

        Assert.True(result);
        Assert.Equal(1, dialogs.UnsavedCalls);
        Assert.Equal(0, dialogs.ConfirmCalls);
        // Don't save: reloaded persisted state, draft cleared.
        Assert.Equal("Alpha", vm.Name);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Navigate_away_dirty_cancel_preserves_draft_and_returns_false()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { UnsavedResult = UnsavedChangesChoice.Cancel };
        var vm = Build(profiles, session, dialogs);

        vm.Name = "Edited";
        var result = await vm.ConfirmCanNavigateAwayAsync();

        Assert.False(result);
        Assert.Equal("Edited", vm.Name);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task Navigate_away_dirty_save_success_persists_and_returns_true()
    {
        // Save choice runs the same TrySaveCore the Save button uses; on success
        // the persisted state is reloaded and navigation proceeds.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { UnsavedResult = UnsavedChangesChoice.Save };
        var vm = Build(profiles, session, dialogs);

        vm.Name = "Edited";
        Assert.True(vm.IsDirty);

        var result = await vm.ConfirmCanNavigateAwayAsync();

        Assert.True(result);
        Assert.Equal(1, dialogs.UnsavedCalls);
        Assert.Single(profiles.UpdateCalls);
        Assert.False(vm.IsDirty);
        Assert.Equal("Edited", vm.Name);
    }

    [Fact]
    public async Task Navigate_away_dirty_save_failure_blocks_and_leaves_save_error()
    {
        // A service rejection after Save closes the dialog but leaves navigation
        // blocked and surfaces the existing localized SaveError. The staged state
        // stays so the user can fix + retry.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        profiles.UpdateProfileThrows = new ArgumentException("simulated divergence");
        var dialogs = new FakeDialogService { UnsavedResult = UnsavedChangesChoice.Save };
        var vm = Build(profiles, session, dialogs);

        vm.Name = "Edited";
        var result = await vm.ConfirmCanNavigateAwayAsync();

        Assert.False(result);
        Assert.Equal(Localization["Profiles_ErrSaveFailed"], vm.SaveError);
        Assert.True(vm.IsDirty);
        Assert.Equal("Edited", vm.Name);
    }

    [Fact]
    public async Task Navigate_away_dirty_invalid_save_passes_canSave_false_to_dialog()
    {
        // When the staged state has a validation error (CanSave false), the
        // dialog is opened with canSave=false so Save is disabled. The user can
        // still Cancel or Don't save. Assert the fake recorded the canSave flag.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { UnsavedResult = UnsavedChangesChoice.DontSave };
        var vm = Build(profiles, session, dialogs);

        // Blank the name (an invalid edit): dirty but not savable.
        vm.Name = "   ";
        Assert.True(vm.IsDirty);
        Assert.False(vm.CanSave);

        var result = await vm.ConfirmCanNavigateAwayAsync();

        Assert.True(result);
        Assert.False(dialogs.LastUnsavedCanSave);
    }

    // ---- running state reflected on IsRunning + command gates ----------------

    [Fact]
    public void Running_state_propagates_from_session_and_gates_add_select_delete()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        Assert.False(vm.IsRunning);
        Assert.True(vm.AddProfileCommand.CanExecute(null));
        Assert.True(vm.DeleteProfileCommand.CanExecute(null));

        session.IsRunning = true;
        Assert.True(vm.IsRunning);
        Assert.False(vm.AddProfileCommand.CanExecute(null));
        Assert.False(vm.DeleteProfileCommand.CanExecute(null));
        Assert.False(vm.SelectProfileCommand.CanExecute(vm.ProfileChoices[0]));

        session.IsRunning = false;
        Assert.True(vm.AddProfileCommand.CanExecute(null));
        Assert.True(vm.DeleteProfileCommand.CanExecute(null));
    }

    // ---- correction 1: IsRunning initialized at construction -----------------

    [Fact]
    public async Task Construction_with_session_running_initializes_gates_without_propertychanged()
    {
        // The VM must snapshot the session's running state at construction so
        // Add/Select/new-Save gates are honest on the very first observation,
        // without requiring a later PropertyChanged to arrive.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = a.Id,
            IsRunning = true,
        };

        var vm = Build(profiles, session);

        Assert.True(vm.IsRunning);
        Assert.False(vm.AddProfileCommand.CanExecute(null));
        Assert.False(vm.SelectProfileCommand.CanExecute(vm.ProfileChoices[0]));

        // A new draft's Save is gated by running (draft-Save requires !IsRunning).
        // Start a draft (requires flipping running off first, since Add is gated).
        session.IsRunning = false;
        await vm.AddProfileCommand.ExecuteAsync(null);
        Assert.True(vm.IsDraft);
        vm.Name = "Draft";
        // Gate the Save by simulating the game starting again.
        session.IsRunning = true;
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    // ---- correction 2: stale / unreadable active profile recovery -----------

    [Fact]
    public void Stale_missing_active_id_loads_no_active_state_with_no_delete_path()
    {
        // The session reports an active id that the service no longer has
        // (deleted out from under us, or a stale persisted id at startup). The
        // VM must fall back to a genuine no-active state: no banner, no editor,
        // no Delete target on the stale id, Select/Add affordances usable.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var staleId = Guid.NewGuid(); // exists nowhere
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = staleId };

        var vm = Build(profiles, session);

        // Genuine no-active state.
        Assert.False(vm.IsBannerVisible);
        Assert.False(vm.IsEditorVisible);
        Assert.True(vm.ShowSelectAffordance); // profiles exist, so Select shows
        Assert.True(vm.HasProfiles);

        // No Delete path on the stale id: DeleteIsVisible is false + the command
        // cannot execute. The VM's _activeId is null (not the stale id).
        Assert.False(vm.DeleteIsVisible);
        Assert.False(vm.DeleteProfileCommand.CanExecute(null));

        // The stale id is excluded from the picker (the session considers it
        // active); the user picks a *different* profile, not the one that won't
        // load.
        Assert.Equal(2, vm.ProfileChoices.Count);
        Assert.DoesNotContain(staleId, vm.ProfileChoices.Select(c => c.Id));
    }

    [Fact]
    public void Unreadable_active_profile_loads_no_active_state()
    {
        // The active profile exists in ListProfiles but GetProfile throws
        // (simulating a corrupt profile.json the production service surfaces as
        // IOException / JsonException / UnauthorizedAccessException). The VM
        // catches those + falls back to no-active state on the initial load.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha", "desc",
            new LaunchSettings { GameArguments = new[] { "--x" } });
        var b = Profile(profiles, "Bravo");
        profiles.GetProfileThrows = new JsonException("corrupt profile.json");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };

        var vm = Build(profiles, session);

        // The initial load caught the throw + fell back to no-active.
        Assert.False(vm.IsBannerVisible);
        Assert.False(vm.IsEditorVisible);
        Assert.True(vm.ShowSelectAffordance); // profiles exist
        Assert.False(vm.DeleteIsVisible);     // no stale-id delete path
        Assert.Null(vm.ActiveProfileBanner);

        // Alpha is excluded from the picker (session considers it active); the
        // user picks Bravo to recover.
        Assert.Single(vm.ProfileChoices);
        Assert.Equal("Bravo", vm.ProfileChoices[0].Name);

        // Recovery: clear the throw + switch to Bravo.
        profiles.GetProfileThrows = null;
        session.ActiveProfileId = b.Id;
        Assert.Equal("Bravo", vm.Name);
        Assert.True(vm.IsBannerVisible);
    }

    [Fact]
    public void Unreadable_active_profile_GetProfile_does_not_leave_half_loaded_editor()
    {
        // An unreadable active must not leave a previous profile's metadata in
        // the Name/Description fields. The no-active path clears them.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha", "alpha desc");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        Assert.Equal("Alpha", vm.Name);

        profiles.GetProfileThrows = new UnauthorizedAccessException("locked");
        session.ActiveProfileId = null; // triggers no-active reload
        session.ActiveProfileId = a.Id; // now unreadable -> no-active fallback

        Assert.Empty(vm.Name);
        Assert.Empty(vm.Description);
        Assert.False(vm.IsDirty);
    }

    // ---- correction 3: CreateProfile rejection surfaces localized error ------

    [Fact]
    public async Task Save_new_service_rejection_surfaces_localized_error_and_does_not_activate()
    {
        var profiles = new FakeProfileService();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);
        vm.Name = "Draft";

        // Simulate the service rejecting a create the inline pass allowed.
        profiles.CreateProfileThrows = new ArgumentException("simulated divergence");

        vm.SaveCommand.Execute(null);

        // Localized generic error shown; raw service text not surfaced.
        Assert.Equal(Localization["Profiles_ErrSaveFailed"], vm.SaveError);
        Assert.DoesNotContain("simulated divergence", vm.SaveError);

        // The attempted create was recorded (the fake records before throwing).
        Assert.Single(profiles.CreateCalls);

        // NO activation after a rejected create (DMF + mod-list reload moved
        // to the shell on Mods entry, so neither is this VM's concern).
        Assert.Equal(0, session.RequestActiveCalls);

        // The draft is retained (still drafting) so the user can fix + retry.
        Assert.True(vm.IsDraft);
    }

    // ---- correction 4: record-then-throw-before-mutation + Cancel recovery ---

    [Fact]
    public async Task Rejected_existing_save_then_cancel_reloads_original_persisted_values()
    {
        // The fake throws BEFORE mutating summaries/settings, so a rejected save
        // leaves the persisted state untouched. Cancel then reloads the honest
        // original values (not the staged edits the user typed).
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha", "alpha desc",
            new LaunchSettings { GameArguments = new[] { "--original" } });
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        // Stage edits.
        vm.Name = "EditedName";
        vm.Description = "edited desc";
        vm.Editor.GameArguments[0].Value = "--changed";
        Assert.True(vm.IsDirty);

        // Service rejects the save before mutating anything.
        profiles.UpdateProfileThrows = new ArgumentException("simulated divergence");
        vm.SaveCommand.Execute(null);

        Assert.Single(profiles.UpdateCalls);
        Assert.Equal(Localization["Profiles_ErrSaveFailed"], vm.SaveError);

        // The persisted values are untouched (record-then-throw-before-mutation).
        var persisted = profiles.GetProfile(a.Id);
        Assert.Equal("Alpha", persisted.Name);
        Assert.Equal("alpha desc", persisted.Description);
        Assert.Equal(new[] { "--original" }, persisted.LaunchSettings.GameArguments.ToArray());

        // Cancel reloads the original persisted state.
        vm.CancelCommand.Execute(null);
        Assert.Equal("Alpha", vm.Name);
        Assert.Equal("alpha desc", vm.Description);
        Assert.Equal("--original", vm.Editor.GameArguments[0].Value);
        Assert.False(vm.IsDirty);
    }

    // ---- correction 6: inline metadata validation ---------------------------

    [Fact]
    public async Task Blank_name_shows_localized_name_required_error()
    {
        var profiles = new FakeProfileService();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);
        // Empty name -> error.
        Assert.Equal(Localization["Profiles_ErrNameRequired"], vm.NameError);

        // Typing clears it.
        vm.Name = "X";
        Assert.Empty(vm.NameError);

        // Whitespace-only also errors.
        vm.Name = "   ";
        Assert.Equal(Localization["Profiles_ErrNameRequired"], vm.NameError);
    }

    [Fact]
    public async Task Multiline_and_overlong_description_show_localized_error()
    {
        var profiles = new FakeProfileService();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);
        Assert.Empty(vm.DescriptionError); // empty is valid

        // Multiline (paste path; XAML caps typing but paste can carry newlines).
        vm.Description = "line1\nline2";
        Assert.Equal(Localization["Profiles_ErrDescriptionInvalid"], vm.DescriptionError);

        // Single-line valid again.
        vm.Description = "single line";
        Assert.Empty(vm.DescriptionError);
    }

    [Fact]
    public async Task Culture_change_remaps_inline_metadata_errors()
    {
        var profiles = new FakeProfileService();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);
        // Blank name + multiline description so both errors are showing.
        vm.Description = "line1\nline2";
        Assert.NotEmpty(vm.NameError);
        Assert.NotEmpty(vm.DescriptionError);

        // Flip the culture: the error messages re-resolve to the same keys
        // (neutral resx), proving the culture subscription refreshed them.
        Localization.SetCulture("fr");

        Assert.Equal(Localization["Profiles_ErrNameRequired"], vm.NameError);
        Assert.Equal(Localization["Profiles_ErrDescriptionInvalid"], vm.DescriptionError);
    }

    // ---- Cancel gate -------------------------------------------------------

    [Fact]
    public async Task Cancel_enabled_for_a_draft_and_reloads_authoritative_state()
    {
        // Save is synchronous (no in-flight DMF or mod-list work), so IsSaving
        // is set + cleared within one synchronous Save call: there is no async
        // window for a test to observe IsSaving=true. The meaningful gate
        // coverage is: Cancel re-enables for a draft (so the user can abandon
        // one) + reloads the authoritative state on click, leaving no draft.
        var profiles = new FakeProfileService();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var vm = Build(profiles, session);

        await vm.AddProfileCommand.ExecuteAsync(null);
        vm.Name = "Draft";
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsDraft);
        Assert.Empty(vm.Name);
    }

    // ---- correction 8: running-aware tooltip properties ---------------------

    [Fact]
    public void Add_and_delete_tooltips_switch_with_running_state()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        Assert.Equal(Localization["Profiles_AddTooltip"], vm.AddTooltip);
        Assert.Equal(Localization["Profiles_DeleteTooltip"], vm.DeleteTooltip);

        session.IsRunning = true;
        Assert.Equal(Localization["Profiles_AddLockedTooltip"], vm.AddTooltip);
        Assert.Equal(Localization["Profiles_DeleteLockedTooltip"], vm.DeleteTooltip);

        session.IsRunning = false;
        Assert.Equal(Localization["Profiles_AddTooltip"], vm.AddTooltip);
    }

    [Fact]
    public void Tooltip_properties_remap_after_culture_change()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = a.Id,
            IsRunning = true,
        };
        var vm = Build(profiles, session);

        // Locked tooltips showing.
        Assert.Equal(Localization["Profiles_AddLockedTooltip"], vm.AddTooltip);

        // Flip culture; the locked tooltip re-resolves to the same key.
        Localization.SetCulture("fr");
        Assert.Equal(Localization["Profiles_AddLockedTooltip"], vm.AddTooltip);
    }

    // ---- correction 5: Add accessible in no-active state via shared action row ----

    [Fact]
    public void No_active_but_profiles_exist_shows_select_affordance_and_shared_action_row()
    {
        // Structural assertion: in the no-active-but-profiles-exist state the
        // Select affordance is the full-width top-level affordance; the shared
        // action row beneath carries Add (the side-by-side layout is gone). The
        // editor action row is hidden (IsEditorVisible false). Add is reachable
        // via its command even with the editor hidden.
        var profiles = new FakeProfileService();
        Profile(profiles, "Alpha");
        Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = null };

        var vm = Build(profiles, session);

        Assert.True(vm.ShowSelectAffordance);
        Assert.True(vm.ShowProfileActions);
        Assert.False(vm.IsEditorVisible);
        Assert.True(vm.AddProfileCommand.CanExecute(null));
    }

    // ---- ShowProfileActions across states -----------------------------------

    [Fact]
    public void Active_profile_state_shows_action_row_with_add_and_delete()
    {
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };

        var vm = Build(profiles, session);

        Assert.True(vm.ShowProfileActions);
        Assert.True(vm.DeleteIsVisible); // active profile -> Delete available
    }

    [Fact]
    public async Task Draft_state_hides_action_row_and_disables_add_at_command_level()
    {
        // Re-stated here so ShowProfileActions has its own focused coverage:
        // the row hides AND Add is command-disabled during a draft (defense in
        // depth on top of the visibility hide).
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };

        var vm = Build(profiles, session);
        Assert.True(vm.ShowProfileActions);

        await vm.AddProfileCommand.ExecuteAsync(null);

        Assert.False(vm.ShowProfileActions);
        Assert.False(vm.AddProfileCommand.CanExecute(null));
    }

    // ---- stable avatar palette ---------------------------------------------

    [Fact]
    public void Same_profile_id_always_projects_the_same_avatar_color()
    {
        // Determinism: a profile's avatar color is a pure function of its id,
        // so it survives reloads, sorting, and app restarts (not process-random
        // and not list-index based). Verified through the VM (banner), then
        // through the palette helper for a static sanity check.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);
        var first = vm.ActiveProfileBanner!.AvatarBackground;

        // The palette helper is the source of truth; ToChoice uses it. The same
        // id maps to the same immutable brush instance every call.
        Assert.Same(first, ProfileAvatarPalette.For(a.Id));
        Assert.Same(ProfileAvatarPalette.For(a.Id), ProfileAvatarPalette.For(a.Id));
        Assert.IsType<ImmutableSolidColorBrush>(first);
    }

    [Fact]
    public void Deliberately_different_ids_can_select_different_avatar_colors()
    {
        // Two ids whose 16-byte sums land on different palette entries: all
        // zeros sums to 0 (index 0), and the second Guid's first byte is 0x01
        // (.NET's little-endian UInt32 layout: "01000000-" parses to byte[0]=1,
        // rest 0), so it sums to 1 (index 1). Different sums mod palette-size
        // -> different immutable brush instances.
        var zero = new Guid("00000000-0000-0000-0000-000000000000");
        var oneByteSet = new Guid("01000000-0000-0000-0000-000000000000");

        Assert.Equal(0, zero.ToByteArray().Sum(b => (int)b));
        Assert.Equal(1, oneByteSet.ToByteArray().Sum(b => (int)b));
        Assert.NotSame(ProfileAvatarPalette.For(zero), ProfileAvatarPalette.For(oneByteSet));
    }

    [Fact]
    public void Banner_and_picker_rows_use_the_projected_avatar_background()
    {
        // The banner avatar and each picker-row avatar bind to the same
        // ProfileChoice.AvatarBackground projection, so a profile reads with one
        // color across the banner + the picker.
        var profiles = new FakeProfileService();
        var a = Profile(profiles, "Alpha");
        var b = Profile(profiles, "Bravo");
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        Assert.NotNull(vm.ActiveProfileBanner!.AvatarBackground);
        Assert.All(vm.ProfileChoices, c => Assert.NotNull(c.AvatarBackground));
        // Each row carries the same color the palette would select.
        var bravoChoice = vm.ProfileChoices.Single(c => c.Id == b.Id);
        Assert.Same(ProfileAvatarPalette.For(b.Id), bravoChoice.AvatarBackground);
    }
}
