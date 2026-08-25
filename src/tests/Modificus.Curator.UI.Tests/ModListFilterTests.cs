using Modificus.Curator.Config;
using Modificus.Curator.Integrations;
using Modificus.Curator.Profiles;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The mod list's filter/search projection: the hide-disabled toggle, the
/// updates-only toggle, and the name search rebuild
/// <see cref="ModListViewModel.VisibleMods"/> from the authoritative
/// <see cref="ModListViewModel.Mods"/> (which stays the full list), the state
/// is session-transient (surviving reloads, cleared on a profile switch), the
/// no-matches state is exclusive with the add-hints empty state, and
/// reordering works THROUGH the projection (move + drag targets in visible
/// space, committed to the stored order with hidden rows keeping their
/// relative order and exactly one SetModOrder call on a real change).
/// </summary>
public sealed class ModListFilterTests
{
    private static readonly LocalizationService Localization = new();

    private static ModListViewModel Build(
        FakeProfileService profiles, FakeProfileSession session, FakeModRepository repo)
        => TestDoubles.BuildModList(profiles, session, repo,
            new FakeModImportService(repo), dialogs: null, localization: Localization);

    private static ProfileSummary Profile(string name) => new(Guid.NewGuid(), name, "");

    private static ModItemViewModel Row(ModListViewModel vm, string name) =>
        vm.Mods.Single(m => m.Name == name);

    private static Guid Id(ModListViewModel vm, string name) => Row(vm, name).ContainerId;

    /// <summary>
    /// Seeds a profile + repository with named mods in the given order; the
    /// enabled array marks which rows are disabled (the hide-filter's target).
    /// Returns everything the test needs.
    /// </summary>
    private static (FakeProfileService Profiles, ProfileSummary Profile, FakeModRepository Repo)
        SeedProfile(string[] names, bool[]? enabled = null, bool[]? locked = null)
    {
        var profile = Profile("Alpha");
        var profiles = TestDoubles.Profiles(profile);
        var repo = new FakeModRepository();
        var entries = new ModListEntry[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var c = repo.Seed(new UntrackedSource(), names[i], "1.0");
            entries[i] = new ModListEntry
            {
                ContainerId = c.Id,
                Enabled = enabled is null || enabled[i],
                Order = i,
                OrderLocked = locked is not null && locked[i],
                Policy = ModVersionPolicy.Latest,
            };
        }

        profiles.WithMods(profile.Id, entries);
        return (profiles, profile, repo);
    }

    private static ModListViewModel BuildView(
        FakeProfileService profiles, ProfileSummary profile, FakeModRepository repo)
        => Build(profiles, new FakeProfileSession { ActiveProfileId = profile.Id }, repo);

    /// <summary>Builds the VM with explicit update-check + update-state seams
    /// so the updates-only tests can seed persisted known-update flags before
    /// construction (the construction-Reload hydrates them) and raise completed
    /// checks after.</summary>
    private static ModListViewModel BuildWithUpdates(
        FakeProfileService profiles, FakeProfileSession session, FakeModRepository repo,
        FakeUpdateCheckService? updateCheck = null, FakeUpdateStateStore? updateState = null)
        => TestDoubles.BuildModList(profiles, session, repo,
            new FakeModImportService(repo), dialogs: null, localization: Localization,
            updateCheck: updateCheck, updateState: updateState);

    /// <summary>A successful check result flagging exactly the given
    /// mods.</summary>
    private static UpdateCheckResult SuccessResult(params ModUpdateInfo[] updates) =>
        new(updates, DateTimeOffset.UtcNow, RateLimited: false, Thorough: false,
            Outcome: CheckOutcome.Success);

    /// <summary>A store with the given containers recorded as the profile's
    /// flagged set (a successful result's Updates become the flagged ids).</summary>
    private static FakeUpdateStateStore FlaggedStore(
        FakeModRepository repo, Guid profileId, params Guid[] containerIds)
    {
        var store = new FakeUpdateStateStore(repo);
        store.RecordResult(profileId, SuccessResult(
            containerIds.Select(id => new ModUpdateInfo(id, 0, "", "1.0", DateTimeOffset.UtcNow)).ToArray()));
        return store;
    }

    private static Guid ContainerId(FakeModRepository repo, string name) =>
        repo.FindUntrackedByName(name)!.Id;

    // ---- projection: filter + search ----------------------------------------

    [Fact]
    public void HideDisabledMods_removes_disabled_rows_from_the_projection_only()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "D1", "B", "D2"],
            enabled: [true, false, true, false]);
        var vm = BuildView(profiles, profile, repo);

        vm.HideDisabledMods = true;

        Assert.Equal(["A", "B"], vm.VisibleMods.Select(m => m.Name));
        // The full list is untouched: the projection is view state over the
        // authoritative order.
        Assert.Equal(4, vm.Mods.Count);
        Assert.True(vm.HasVisibleMods);
    }

    [Fact]
    public void SearchText_filters_by_name_case_insensitive_substring()
    {
        var (profiles, profile, repo) = SeedProfile(["Weapon Overhaul", "Sound Pack", "Armory"]);
        var vm = BuildView(profiles, profile, repo);

        // Case-insensitive substring, ordinal.
        vm.SearchText = "overhaul";
        Assert.Equal(["Weapon Overhaul"], vm.VisibleMods.Select(m => m.Name));
        vm.SearchText = "SOUND";
        Assert.Equal(["Sound Pack"], vm.VisibleMods.Select(m => m.Name));
        vm.SearchText = "ry";
        Assert.Equal(["Armory"], vm.VisibleMods.Select(m => m.Name));

        // Whitespace-only search matches everything but still counts as typed
        // text for the clear affordance.
        vm.SearchText = "   ";
        Assert.Equal(3, vm.VisibleMods.Count);
        Assert.True(vm.HasSearchText);
        Assert.False(vm.IsFilterOrSearchActive);
    }

    [Fact]
    public void Filter_and_search_combine_with_AND()
    {
        var (profiles, profile, repo) = SeedProfile(["Weapon Overhaul", "Sound Pack", "Armory"],
            enabled: [true, true, false]);
        var vm = BuildView(profiles, profile, repo);

        vm.HideDisabledMods = true;
        vm.SearchText = "pack";

        Assert.Equal(["Sound Pack"], vm.VisibleMods.Select(m => m.Name));
    }

    [Fact]
    public void Clearing_the_filter_and_search_restores_the_full_projection()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "D"],
            enabled: [true, false]);
        var vm = BuildView(profiles, profile, repo);
        vm.HideDisabledMods = true;
        vm.SearchText = "zzz";

        Assert.Empty(vm.VisibleMods);
        Assert.False(vm.HasVisibleMods);

        vm.SearchText = string.Empty;
        vm.HideDisabledMods = false;

        Assert.Equal(["A", "D"], vm.VisibleMods.Select(m => m.Name));
        Assert.True(vm.HasVisibleMods);
    }

    [Fact]
    public void Without_a_filter_the_projection_mirrors_the_full_list()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "D"],
            enabled: [true, false]);
        var vm = BuildView(profiles, profile, repo);

        Assert.Equal(vm.Mods, vm.VisibleMods);
        Assert.All(vm.VisibleMods, row => Assert.Contains(row, vm.Mods));
    }

    [Fact]
    public void The_projection_survives_a_reload()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "D", "B"],
            enabled: [true, false, true]);
        var vm = BuildView(profiles, profile, repo);
        vm.HideDisabledMods = true;
        vm.SearchText = string.Empty;

        // A reload from any trigger (a lock toggle here, like production use)
        // keeps the filter state; the rebuilt rows re-project.
        vm.ToggleOrderLockCommand.Execute(Row(vm, "A"));

        Assert.True(vm.HideDisabledMods);
        Assert.Equal(["A", "B"], vm.VisibleMods.Select(m => m.Name));
    }

    [Fact]
    public void A_profile_switch_clears_the_filter_and_search()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "D"],
            enabled: [true, false]);
        var other = Profile("Beta");
        profiles.WithProfile("Beta");
        var otherContainer = repo.Seed(new UntrackedSource(), "OtherMod", "1.0");
        profiles.WithMods(other.Id, new ModListEntry { ContainerId = otherContainer.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);
        vm.HideDisabledMods = true;
        vm.SearchText = "never-matches";

        session.ActiveProfileId = other.Id;

        Assert.Equal(string.Empty, vm.SearchText);
        Assert.False(vm.HideDisabledMods);
        Assert.False(vm.IsFilterOrSearchActive);
        // The new profile's rows are visible without a filter.
        Assert.Equal(["OtherMod"], vm.VisibleMods.Select(m => m.Name));
    }

    [Fact]
    public void ToggleEnabled_under_the_hide_filter_removes_the_row_from_the_projection()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "D"],
            enabled: [true, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);
        vm.HideDisabledMods = true;
        Assert.Equal(["A"], vm.VisibleMods.Select(m => m.Name));

        var a = Row(vm, "A");
        a.Enabled = false;
        session.HasPendingChanges = false;
        vm.ToggleEnabledCommand.Execute(a);

        Assert.Equal(["A", "D"], vm.Mods.Select(m => m.Name));
        Assert.Empty(vm.VisibleMods);
        // The whole visible set emptied under an active filter: the no-matches
        // state, not the add-a-mod state.
        Assert.True(vm.ShowNoMatchesMessage);
        Assert.True(session.HasPendingChanges);
    }

    // ---- no-matches vs empty-state exclusivity --------------------------------

    [Fact]
    public void No_matches_shows_only_when_the_full_list_is_nonempty_and_the_projection_is_empty()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "D"],
            enabled: [true, false]);
        var vm = BuildView(profiles, profile, repo);

        // Not with a non-empty visible set...
        vm.SearchText = "a";
        Assert.False(vm.ShowNoMatchesMessage);
        // ...not without a filter/search...
        vm.SearchText = string.Empty;
        Assert.False(vm.ShowNoMatchesMessage);
        // ...yes when the search empties the visible set of a non-empty list.
        vm.SearchText = "zzz";
        Assert.True(vm.ShowNoMatchesMessage);
        // ...but the hide filter alone does not empty this list (A stays
        // visible), so it must not trigger the message either.
        vm.SearchText = string.Empty;
        vm.HideDisabledMods = true;
        Assert.False(vm.ShowNoMatchesMessage);
    }

    [Fact]
    public void The_add_hint_is_suppressed_while_a_filter_or_search_is_active()
    {
        // One disabled mod: normally the DMF-only-style add hint shows; under
        // the hide filter the visible set empties and the no-matches message
        // owns the content area instead.
        var (profiles, profile, repo) = SeedProfile(["D"], enabled: [false]);
        var vm = BuildView(profiles, profile, repo);
        Assert.True(vm.ShowAddModsHint);

        vm.HideDisabledMods = true;

        Assert.False(vm.ShowAddModsHint);
        Assert.True(vm.ShowNoMatchesMessage);

        vm.HideDisabledMods = false;

        Assert.True(vm.ShowAddModsHint);
        Assert.False(vm.ShowNoMatchesMessage);
    }

    [Fact]
    public void No_matches_stays_false_with_no_active_profile()
    {
        var profiles = TestDoubles.Profiles();
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null },
            new FakeModRepository());

        vm.HideDisabledMods = true;
        vm.SearchText = "x";

        Assert.False(vm.ShowNoMatchesMessage);
        Assert.Empty(vm.VisibleMods);
    }

    // ---- updates-only filter ---------------------------------------------------

    [Fact]
    public void ShowUpdatesOnly_keeps_flagged_rows_in_the_projection_only()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "B", "C", "D"]);
        var updateState = FlaggedStore(repo, profile.Id,
            ContainerId(repo, "A"), ContainerId(repo, "C"));
        var vm = BuildWithUpdates(profiles,
            new FakeProfileSession { ActiveProfileId = profile.Id }, repo,
            updateState: updateState);

        vm.ShowUpdatesOnly = true;

        Assert.Equal(["A", "C"], vm.VisibleMods.Select(m => m.Name));
        // The full list is untouched: the projection is view state over the
        // authoritative order.
        Assert.Equal(4, vm.Mods.Count);
        Assert.True(vm.HasVisibleMods);

        // ORDERING regression: a Reload under the active filter must hydrate
        // the known-update flags BEFORE rebuilding the projection, or the
        // freshly built rows' all-false flags empty VisibleMods.
        vm.Reload();
        Assert.True(vm.ShowUpdatesOnly);
        Assert.Equal(["A", "C"], vm.VisibleMods.Select(m => m.Name));
    }

    [Fact]
    public void Updates_only_filter_and_search_combine_with_AND()
    {
        // Alpha/Beta/Delta enabled, Gamma disabled; Alpha/Gamma/Delta flagged.
        var (profiles, profile, repo) = SeedProfile(["Alpha", "Beta", "Gamma", "Delta"],
            enabled: [true, true, false, true]);
        var updateState = FlaggedStore(repo, profile.Id,
            ContainerId(repo, "Alpha"), ContainerId(repo, "Gamma"), ContainerId(repo, "Delta"));
        var vm = BuildWithUpdates(profiles,
            new FakeProfileSession { ActiveProfileId = profile.Id }, repo,
            updateState: updateState);

        vm.ShowUpdatesOnly = true;
        Assert.Equal(["Alpha", "Gamma", "Delta"], vm.VisibleMods.Select(m => m.Name));

        // The hide filter removes the disabled flagged row too.
        vm.HideDisabledMods = true;
        Assert.Equal(["Alpha", "Delta"], vm.VisibleMods.Select(m => m.Name));

        // The search narrows to the intersection.
        vm.SearchText = "al";
        Assert.Equal(["Alpha"], vm.VisibleMods.Select(m => m.Name));
    }

    [Fact]
    public void A_landed_check_reprojects_the_updates_only_filter_live()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "B"]);
        var updateCheck = new FakeUpdateCheckService();
        var updateState = new FakeUpdateStateStore(repo);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = BuildWithUpdates(profiles, session, repo,
            updateCheck: updateCheck, updateState: updateState);

        // Zero flags: the filter empties the projection (the no-matches state).
        vm.ShowUpdatesOnly = true;
        Assert.Empty(vm.VisibleMods);
        Assert.True(vm.ShowNoMatchesMessage);

        // A check lands flagging A: the row appears without touching the filter.
        updateCheck.RaiseCheckCompleted(SuccessResult(
            new ModUpdateInfo(ContainerId(repo, "A"), 0, "A", "1.0", DateTimeOffset.UtcNow)),
            profile.Id);
        Assert.Equal(["A"], vm.VisibleMods.Select(m => m.Name));
        Assert.False(vm.ShowNoMatchesMessage);

        // A second check clears the updates: the projection empties again.
        updateCheck.RaiseCheckCompleted(SuccessResult(), profile.Id);
        Assert.Empty(vm.VisibleMods);
        Assert.True(vm.ShowNoMatchesMessage);
    }

    [Fact]
    public void The_updates_only_filter_survives_a_reload_and_clears_on_a_profile_switch()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "B"]);
        var updateState = FlaggedStore(repo, profile.Id, ContainerId(repo, "A"));
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = BuildWithUpdates(profiles, session, repo, updateState: updateState);
        vm.ShowUpdatesOnly = true;
        Assert.Equal(["A"], vm.VisibleMods.Select(m => m.Name));

        vm.Reload();
        Assert.True(vm.ShowUpdatesOnly);
        Assert.Equal(["A"], vm.VisibleMods.Select(m => m.Name));

        var other = Profile("Beta");
        profiles.WithProfile("Beta");
        var otherContainer = repo.Seed(new UntrackedSource(), "OtherMod", "1.0");
        profiles.WithMods(other.Id, new ModListEntry { ContainerId = otherContainer.Id, Order = 0 });
        session.ActiveProfileId = other.Id;

        Assert.False(vm.ShowUpdatesOnly);
        // The new profile's rows are visible without a filter.
        Assert.Equal(["OtherMod"], vm.VisibleMods.Select(m => m.Name));
    }

    [Fact]
    public void Updates_only_with_zero_flags_is_the_no_matches_state_not_the_add_hint()
    {
        // One unflagged mod: the add hint shows for a zero-or-one-mod list;
        // the active filter flips the empty state to no-matches (the two are
        // mutually exclusive).
        var (profiles, profile, repo) = SeedProfile(["A"]);
        var vm = BuildWithUpdates(profiles,
            new FakeProfileSession { ActiveProfileId = profile.Id }, repo,
            updateState: new FakeUpdateStateStore(repo));
        Assert.True(vm.ShowAddModsHint);

        vm.ShowUpdatesOnly = true;

        Assert.True(vm.ShowNoMatchesMessage);
        Assert.False(vm.ShowAddModsHint);

        vm.ShowUpdatesOnly = false;

        Assert.False(vm.ShowNoMatchesMessage);
        Assert.True(vm.ShowAddModsHint);
    }

    [Fact]
    public void The_updates_only_tooltip_flips_with_the_toggle()
    {
        var (profiles, profile, repo) = SeedProfile(["A"]);
        var vm = BuildView(profiles, profile, repo);

        Assert.Equal(Localization["ModList_ShowUpdatesOnlyTooltip"], vm.UpdatesOnlyTooltip);
        vm.ShowUpdatesOnly = true;
        Assert.Equal(Localization["ModList_ShowAllModsTooltip"], vm.UpdatesOnlyTooltip);
    }

    // ---- move availability under the projection -------------------------------

    [Fact]
    public void Move_buttons_follow_visible_unlocked_neighbors()
    {
        // [A, D1, D2, B] with D1/D2 disabled + hidden: A and B are visible
        // unlocked neighbors of each other despite the hidden rows between.
        var (profiles, profile, repo) = SeedProfile(["A", "D1", "D2", "B"],
            enabled: [true, false, false, true]);
        var vm = BuildView(profiles, profile, repo);
        vm.HideDisabledMods = true;

        Assert.True(Row(vm, "A").CanMoveDown);
        Assert.True(Row(vm, "B").CanMoveUp);
    }

    [Fact]
    public void A_row_with_only_hidden_or_locked_rows_above_cannot_move_up()
    {
        // [D(hidden), A]: A is the top visible unlocked row.
        var (profiles, profile, repo) = SeedProfile(["D", "A"], enabled: [false, true]);
        var vm = BuildView(profiles, profile, repo);
        vm.HideDisabledMods = true;

        Assert.False(Row(vm, "A").CanMoveUp);
        Assert.False(Row(vm, "A").CanMoveDown);
    }

    // ---- reorder through the filter -------------------------------------------

    [Fact]
    public void MoveDown_across_a_hidden_row_commits_one_call_and_keeps_hidden_order()
    {
        // [A, H(disabled+hidden), B]; A down -> [H, B, A]: A lands immediately
        // after B in the stored order, H shifts one slot and stays between the
        // top and the crossed pair.
        var (profiles, profile, repo) = SeedProfile(["A", "H", "B"],
            enabled: [true, false, true]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);
        vm.HideDisabledMods = true;

        session.HasPendingChanges = false;
        vm.MoveDownCommand.Execute(Row(vm, "A"));

        Assert.Equal([Id(vm, "H"), Id(vm, "B"), Id(vm, "A")],
            Assert.Single(profiles.SetModOrderCalls));
        Assert.True(session.HasPendingChanges);
        Assert.Equal(["H", "B", "A"], vm.Mods.Select(m => m.Name));
        Assert.Equal(["B", "A"], vm.VisibleMods.Select(m => m.Name));
    }

    [Fact]
    public void MoveUp_across_a_hidden_row_lands_the_source_adjacent_to_the_crossed_row()
    {
        // [A, H(hidden), B]; B up -> [B, A, H].
        var (profiles, profile, repo) = SeedProfile(["A", "H", "B"],
            enabled: [true, false, true]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);
        vm.HideDisabledMods = true;

        vm.MoveUpCommand.Execute(Row(vm, "B"));

        Assert.Equal([Id(vm, "B"), Id(vm, "A"), Id(vm, "H")],
            Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal(["B", "A", "H"], vm.Mods.Select(m => m.Name));
        Assert.Equal(["B", "A"], vm.VisibleMods.Select(m => m.Name));
    }

    [Fact]
    public void Drag_commit_with_visible_ranks_maps_identically_to_a_move()
    {
        // [A, H(hidden), B, C]; drag A to the end (visible rank 2 = past C)
        // -> [H, B, C, A].
        var (profiles, profile, repo) = SeedProfile(["A", "H", "B", "C"],
            enabled: [true, false, true, true]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);
        vm.HideDisabledMods = true;

        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "A"), 2));

        Assert.Equal(
            [Id(vm, "H"), Id(vm, "B"), Id(vm, "C"), Id(vm, "A")],
            Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal(["B", "C", "A"], vm.VisibleMods.Select(m => m.Name));
    }

    [Fact]
    public void A_locked_row_keeps_its_index_while_reordering_through_a_filter()
    {
        // [L(locked), A, H(hidden), B]; A down -> [L, H, B, A]: L keeps rank 0.
        var (profiles, profile, repo) = SeedProfile(["L", "A", "H", "B"],
            enabled: [true, true, false, true], locked: [true, false, false, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);
        vm.HideDisabledMods = true;

        vm.MoveDownCommand.Execute(Row(vm, "A"));

        Assert.Equal(
            [Id(vm, "L"), Id(vm, "H"), Id(vm, "B"), Id(vm, "A")],
            Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal("L", vm.Mods[0].Name);
        Assert.True(vm.Mods[0].OrderLocked);
    }

    [Fact]
    public void A_noop_drop_under_the_filter_makes_no_service_calls()
    {
        // [A, B, H(hidden)]; A dropped at its own visible rank 0: the hidden
        // row already sits below the source, so the rebuild equals the current
        // order.
        var (profiles, profile, repo) = SeedProfile(["A", "B", "H"],
            enabled: [true, true, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);
        vm.HideDisabledMods = true;

        session.HasPendingChanges = false;
        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "A"), 0));

        Assert.Empty(profiles.SetModOrderCalls);
        Assert.False(session.HasPendingChanges);
    }

    [Fact]
    public void Move_commands_on_a_row_hidden_by_the_filter_are_noops()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "H", "B"],
            enabled: [true, false, true]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);
        vm.HideDisabledMods = true;
        var hidden = Row(vm, "H");

        vm.MoveUpCommand.Execute(hidden);
        vm.MoveDownCommand.Execute(hidden);

        Assert.Empty(profiles.SetModOrderCalls);
    }

    [Fact]
    public void Out_of_range_visible_ranks_are_rejected_under_the_filter()
    {
        // [A, H(hidden), B]: moving A has exactly one visible unlocked other
        // (B), so rank 2 is out of range (it would be valid unfiltered).
        var (profiles, profile, repo) = SeedProfile(["A", "H", "B"],
            enabled: [true, false, true]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);
        vm.HideDisabledMods = true;

        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "A"), 2));
        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "A"), -1));

        Assert.Empty(profiles.SetModOrderCalls);
    }

    [Fact]
    public void The_top_visible_unlocked_row_cannot_move_up_even_with_hidden_rows_above()
    {
        // [D(hidden), A, B]; A is the top visible unlocked row.
        var (profiles, profile, repo) = SeedProfile(["D", "A", "B"],
            enabled: [false, true, true]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);
        vm.HideDisabledMods = true;

        session.HasPendingChanges = false;
        vm.MoveUpCommand.Execute(Row(vm, "A"));

        Assert.Empty(profiles.SetModOrderCalls);
        Assert.False(session.HasPendingChanges);
    }

    [Fact]
    public void ShowUpdatesOnly_keeps_version_unknown_rows()
    {
        // A version-unknown row (Nexus + an empty latest tag) carries no
        // flagged update, but its update action is the resolution path, so
        // the filter keeps it reachable while active.
        var (profiles, profile, repo) = SeedProfile(["A", "B"]);
        var unknown = repo.Seed(new NexusSource { ModId = 8 }, "Unknown", string.Empty);
        profiles.WithMods(profile.Id,
            new ModListEntry { ContainerId = ContainerId(repo, "A"), Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = ContainerId(repo, "B"), Enabled = true, Order = 1, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = unknown.Id, Enabled = true, Order = 2, Policy = ModVersionPolicy.Latest });
        var updateState = FlaggedStore(repo, profile.Id, ContainerId(repo, "A"));
        var vm = BuildWithUpdates(profiles,
            new FakeProfileSession { ActiveProfileId = profile.Id }, repo,
            updateState: updateState);

        var unknownRow = Row(vm, "Unknown");
        Assert.True(unknownRow.IsVersionUnknown);
        Assert.False(unknownRow.UpdateAvailable);

        vm.ShowUpdatesOnly = true;

        // The flagged row + the unknown row stay; the untagged ordinary row
        // leaves.
        Assert.Equal(["A", "Unknown"], vm.VisibleMods.Select(m => m.Name));
        Assert.Equal(3, vm.Mods.Count);
    }
}
