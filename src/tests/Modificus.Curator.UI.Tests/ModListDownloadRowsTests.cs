using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The download-row hosting projection + the in-place morph: which surface
/// renders each queue item (a visible profile row morphs in place;
/// everything else appends below the list), the recompute triggers
/// (coordinator collection/state changes, reload, filter/search, profile
/// switch), the morphed row's command/enabled state, the empty-state
/// suppression, and the structural guarantee that download rows never enter
/// the reorder surfaces. The <see cref="DownloadRowViewModel"/> projections
/// (phase flags, percent, bytes, failure, pulse) are exercised directly.
/// </summary>
public sealed class ModListDownloadRowsTests
{
    private static readonly LocalizationService Localization = new();

    // ---- harness -----------------------------------------------------------

    /// <summary>
    /// Seeds a profile + repository with named mods in the given order.
    /// Returns everything the test needs (the session is built around the
    /// profile so a switch test can move it).
    /// </summary>
    private static (FakeProfileService Profiles, ProfileSummary Profile, FakeModRepository Repo, FakeProfileSession Session)
        SeedProfile(params string[] names)
    {
        var profile = new ProfileSummary(Guid.NewGuid(), "Alpha", "");
        var profiles = TestDoubles.Profiles(profile);
        var repo = new FakeModRepository();
        var entries = new ModListEntry[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var c = repo.Seed(new UntrackedSource(), names[i], "1.0");
            entries[i] = new ModListEntry
            {
                ContainerId = c.Id,
                Enabled = true,
                Order = i,
                Policy = ModVersionPolicy.Latest,
            };
        }

        profiles.WithMods(profile.Id, entries);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        return (profiles, profile, repo, session);
    }

    private static ModListViewModel Build(
        FakeProfileService profiles, FakeProfileSession session, FakeModRepository repo,
        FakeModDownloadQueue queue) =>
        TestDoubles.BuildModList(profiles, session, repo,
            new FakeModImportService(repo), localization: Localization, downloadQueue: queue);

    /// <summary>
    /// Builds a queue item directly (the fake never runs a pipeline): the
    /// shape the real coordinator admits, with the container id, phase, and
    /// name the test wants.
    /// </summary>
    private static DownloadItem Item(
        Guid? containerId,
        string name = "Cool Mod",
        DownloadPhase phase = DownloadPhase.Queued,
        Guid? targetProfileId = null) =>
        new(new ModDownloadRequest(
            "warhammer40kdarktide", 101, 202, DownloadPurpose.ProfileAdd,
            containerId, name, targetProfileId ?? Guid.NewGuid(), "Alpha"))
        {
            DisplayName = name,
            ContainerId = containerId,
            Phase = phase,
        };

    private static ModItemViewModel Row(ModListViewModel vm, string name) =>
        vm.Mods.Single(m => m.Name == name);

    // ---- hosting rule ------------------------------------------------------

    [Fact]
    public void An_item_targeting_a_visible_row_morphs_it_in_place()
    {
        var (profiles, profile, repo, session) = SeedProfile("A", "B");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);

        var item = queue.Add(Item(Row(vm, "A").ContainerId));

        var morphed = Row(vm, "A");
        Assert.NotNull(morphed.ActiveDownload);
        Assert.Same(item, morphed.ActiveDownload!.Item);
        Assert.True(morphed.IsDownloadMorphed);
        Assert.Empty(vm.DownloadRows);
        // The projection is untouched: the download is not a profile row.
        Assert.Equal(2, vm.VisibleMods.Count);
        Assert.All(vm.VisibleMods, row => Assert.IsType<ModItemViewModel>(row));
    }

    [Fact]
    public void The_same_item_with_its_row_filtered_out_renders_appended()
    {
        var (profiles, profile, repo, session) = SeedProfile("A", "B");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);

        // A is hidden by the search; its download cannot morph it.
        vm.SearchText = "b";
        var aId = Row(vm, "A").ContainerId;
        var item = queue.Add(Item(aId));

        Assert.Null(Row(vm, "A").ActiveDownload);
        var wrapper = Assert.Single(vm.DownloadRows);
        Assert.Same(item, wrapper.Item);
        Assert.True(vm.HasDownloadRows);
        Assert.True(vm.HasListContent);

        // Clearing the search re-hosts it in place: the projection is
        // recomputed on every filter/search change, no placement state.
        vm.SearchText = string.Empty;
        Assert.NotNull(Row(vm, "A").ActiveDownload);
        Assert.Same(item, Row(vm, "A").ActiveDownload!.Item);
        Assert.Empty(vm.DownloadRows);

        // And hiding the row again falls back to appended (both directions
        // through the same recompute).
        vm.HideDisabledMods = false;
        Row(vm, "A").Enabled = false;
        vm.ToggleEnabledCommand.Execute(Row(vm, "A"));
        vm.HideDisabledMods = true;
        Assert.Null(Row(vm, "A").ActiveDownload);
        Assert.Same(item, Assert.Single(vm.DownloadRows).Item);
    }

    [Fact]
    public void A_profile_switch_rehosts_both_directions()
    {
        var (profiles, alpha, repo, session) = SeedProfile("A");
        var beta = profiles.WithProfile("Beta");
        var bContainer = repo.Seed(new UntrackedSource(), "B", "1.0");
        profiles.WithMods(beta.Id,
            new ModListEntry { ContainerId = bContainer.Id, Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest });

        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);

        // One item targets Alpha's mod, one targets Beta's mod.
        var forAlpha = queue.Add(Item(Row(vm, "A").ContainerId, "For Alpha", targetProfileId: alpha.Id));
        var forBeta = queue.Add(Item(bContainer.Id, "For Beta", targetProfileId: beta.Id));

        // Under Alpha: the Alpha item morphs, the Beta item appends.
        Assert.NotNull(Row(vm, "A").ActiveDownload);
        Assert.Same(forBeta, Assert.Single(vm.DownloadRows).Item);

        // Switch to Beta: the cross-profile item re-hosts appended, the
        // Beta item takes its row in place.
        session.ActiveProfileId = beta.Id;
        Assert.NotNull(Row(vm, "B").ActiveDownload);
        Assert.Same(forBeta, Row(vm, "B").ActiveDownload!.Item);
        Assert.Same(forAlpha, Assert.Single(vm.DownloadRows).Item);

        // And back.
        session.ActiveProfileId = alpha.Id;
        Assert.NotNull(Row(vm, "A").ActiveDownload);
        Assert.Same(forAlpha, Row(vm, "A").ActiveDownload!.Item);
        Assert.Same(forBeta, Assert.Single(vm.DownloadRows).Item);
    }

    [Fact]
    public void An_item_with_a_null_container_id_always_renders_appended()
    {
        var (profiles, profile, repo, session) = SeedProfile("A", "B");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);

        var item = queue.Add(Item(null));

        Assert.Null(Row(vm, "A").ActiveDownload);
        Assert.Null(Row(vm, "B").ActiveDownload);
        Assert.Same(item, Assert.Single(vm.DownloadRows).Item);

        // Even a reload + filter clear cannot host it: there is no container
        // to join a row by.
        vm.Reload();
        Assert.Same(item, Assert.Single(vm.DownloadRows).Item);
    }

    [Fact]
    public void A_resolve_landing_a_container_id_moves_an_appended_item_in_place()
    {
        var (profiles, profile, repo, session) = SeedProfile("A");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);

        var item = queue.Add(Item(null));
        Assert.Same(item, Assert.Single(vm.DownloadRows).Item);

        // The coordinator resolved the container (the miss path's resolve
        // announcement): the item re-hosts onto the visible row.
        item.ContainerId = Row(vm, "A").ContainerId;
        queue.Publish(item);

        Assert.NotNull(Row(vm, "A").ActiveDownload);
        Assert.Same(item, Row(vm, "A").ActiveDownload!.Item);
        Assert.Empty(vm.DownloadRows);
    }

    [Fact]
    public void Reload_preserves_the_morph_on_the_rebuilt_row()
    {
        var (profiles, profile, repo, session) = SeedProfile("A", "B");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);
        var aId = Row(vm, "A").ContainerId;
        var item = queue.Add(Item(aId));
        var oldRow = Row(vm, "A");
        Assert.NotNull(oldRow.ActiveDownload);

        // Any reload trigger (a lock toggle here) rebuilds the rows; the
        // projection re-derives the morph for the new row instance.
        vm.ToggleOrderLockCommand.Execute(Row(vm, "B"));

        var rebuilt = vm.Mods.Single(r => r.ContainerId == aId);
        Assert.NotSame(oldRow, rebuilt); // rows really were rebuilt
        Assert.Same(item, rebuilt.ActiveDownload!.Item);
        Assert.Empty(vm.DownloadRows);
    }

    [Fact]
    public void A_failed_corpse_and_a_fresh_live_attempt_render_side_by_side()
    {
        var (profiles, profile, repo, session) = SeedProfile("A");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);
        var aId = Row(vm, "A").ContainerId;

        // A failed item stays until dismissed; a fresh click on the same key
        // creates a SECOND live item (the dedupe index released the failed
        // one). Both render exactly once: the corpse morphs the row
        // (visibly terminal), the live attempt appends.
        var corpse = queue.Add(Item(aId, "Cool Mod", DownloadPhase.Failed));
        corpse.ErrorMessage = "The download failed.";
        var live = queue.Add(Item(aId, "Cool Mod"));

        Assert.Same(corpse, Row(vm, "A").ActiveDownload!.Item);
        Assert.Same(live, Assert.Single(vm.DownloadRows).Item);
    }

    [Fact]
    public void Removing_the_item_clears_the_morph_and_the_appended_row()
    {
        var (profiles, profile, repo, session) = SeedProfile("A");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);

        var morphed = queue.Add(Item(Row(vm, "A").ContainerId));
        var appended = queue.Add(Item(null));
        Assert.NotNull(Row(vm, "A").ActiveDownload);
        Assert.Single(vm.DownloadRows);

        queue.Remove(morphed);
        Assert.Null(Row(vm, "A").ActiveDownload);
        queue.Remove(appended);
        Assert.Empty(vm.DownloadRows);
        Assert.False(vm.HasDownloadRows);
        Assert.False(vm.HasActiveDownloads);
    }

    // ---- empty-state suppression + coexistence ------------------------------

    [Fact]
    public void An_active_download_suppresses_the_add_hints_empty_state()
    {
        var (profiles, profile, repo, session) = SeedProfile();
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);
        Assert.True(vm.ShowAddModsHint);

        var item = queue.Add(Item(null));
        Assert.True(vm.HasActiveDownloads);
        Assert.False(vm.ShowAddModsHint);
        Assert.True(vm.HasListContent); // the scroll region hosts the row

        // The item completing (leaving the collection) restores the hint.
        queue.Remove(item);
        Assert.False(vm.HasActiveDownloads);
        Assert.True(vm.ShowAddModsHint);
        Assert.False(vm.HasListContent);
    }

    [Fact]
    public void A_failed_item_does_not_suppress_the_empty_state()
    {
        var (profiles, profile, repo, session) = SeedProfile();
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);

        queue.Add(Item(null, phase: DownloadPhase.Failed));

        // Terminal: no live activity, so the ordinary empty state still
        // reads; the failed row renders above it.
        Assert.False(vm.HasActiveDownloads);
        Assert.True(vm.ShowAddModsHint);
        Assert.Single(vm.DownloadRows);
    }

    [Fact]
    public void The_no_matches_message_coexists_with_appended_rows()
    {
        var (profiles, profile, repo, session) = SeedProfile("A");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);

        vm.SearchText = "zzz";
        Assert.True(vm.ShowNoMatchesMessage);

        queue.Add(Item(null));

        // Both hold: the message owns the projection's emptiness, the
        // appended row still renders below it, and the scroll region stays
        // visible for the row.
        Assert.True(vm.ShowNoMatchesMessage);
        Assert.Single(vm.DownloadRows);
        Assert.True(vm.HasListContent);
    }

    // ---- the morphed row's control + command state ---------------------------

    [Fact]
    public void AMorphed_row_suppresses_policy_and_update_affordances_but_keeps_structural_controls()
    {
        var (profiles, profile, repo, session) = SeedProfile("A", "B");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);
        var a = Row(vm, "A");

        queue.Add(Item(a.ContainerId));

        Assert.True(a.IsDownloadMorphed);
        Assert.False(a.IsPolicyEditable);
        // Structural controls stay functional: the grip follows the lock
        // only, and the move availability is the ordinary projection.
        Assert.True(a.IsGripEnabled);
        Assert.False(a.CanMoveUp); // top row
        Assert.True(a.CanMoveDown);

        // Un-morph (the item leaving the collection) restores the suppressed
        // affordances.
        foreach (var item in queue.Items.ToArray())
        {
            queue.Remove(item);
        }

        Assert.False(a.IsDownloadMorphed);
        Assert.True(a.IsPolicyEditable);
    }

    [Fact]
    public void A_locked_morphed_row_keeps_its_lock_semantics()
    {
        var (profiles, profile, repo, session) = SeedProfile("L", "B");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);
        vm.ToggleOrderLockCommand.Execute(Row(vm, "L"));
        var locked = Row(vm, "L");

        queue.Add(Item(locked.ContainerId));

        Assert.True(locked.IsDownloadMorphed);
        Assert.False(locked.IsGripEnabled); // the lock still owns the grip
        Assert.False(locked.CanMoveUp);
        Assert.False(locked.CanMoveDown);
    }

    [Fact]
    public void Cancel_dismiss_retry_forward_to_the_queue()
    {
        var queue = new FakeModDownloadQueue();
        var item = Item(null);
        var wrapper = new DownloadRowViewModel(Localization, queue, item);

        wrapper.CancelCommand.Execute(null);
        Assert.Same(item, Assert.Single(queue.CancelCalls));

        wrapper.RetryCommand.Execute(null);
        Assert.Same(item, Assert.Single(queue.RetryCalls));

        wrapper.DismissCommand.Execute(null);
        Assert.Same(item, Assert.Single(queue.DismissCalls));
    }

    // ---- the wrapper's render projections ------------------------------------

    [Fact]
    public void Phase_and_byte_projections_cover_every_render_state()
    {
        var queue = new FakeModDownloadQueue();
        var item = Item(null, phase: DownloadPhase.Queued);
        var wrapper = new DownloadRowViewModel(Localization, queue, item);

        // Queued: word only, no bars, no percent, no bytes.
        Assert.True(wrapper.IsQueued);
        Assert.Equal("Queued", wrapper.StatusText);
        Assert.False(wrapper.ShowDeterminateProgress);
        Assert.False(wrapper.ShowIndeterminateProgress);
        Assert.False(wrapper.IsDownloading);
        Assert.True(wrapper.CanCancel);

        // Downloading with a known total: determinate bar + percent + the
        // received/total pair.
        item.Phase = DownloadPhase.Downloading;
        item.ReceivedBytes = 12_884_902; // 12.3 MB
        item.TotalBytes = 28_835_584; // 27.5 MB
        Assert.True(wrapper.IsDownloading);
        Assert.True(wrapper.ShowDeterminateProgress);
        Assert.False(wrapper.ShowIndeterminateProgress);
        Assert.Equal(44.68, wrapper.ProgressPercent, precision: 2);
        Assert.Equal("45%", wrapper.PercentText);
        Assert.Equal("12.3 / 27.5 MB", wrapper.BytesText);
        Assert.StartsWith("Cool Mod: Downloading, 45%", wrapper.AutomationText);

        // Downloading with an unknown total: indeterminate bar, received
        // bytes only, no percent.
        item.TotalBytes = null;
        Assert.False(wrapper.ShowDeterminateProgress);
        Assert.True(wrapper.ShowIndeterminateProgress);
        Assert.Equal("12.3 MB", wrapper.BytesText);

        // Importing: indeterminate bar, no byte text.
        item.Phase = DownloadPhase.Importing;
        Assert.True(wrapper.ShowIndeterminateProgress);
        Assert.False(wrapper.IsDownloading);

        // Failed: the message inline, no cancel.
        item.Phase = DownloadPhase.Failed;
        item.ErrorMessage = "The download failed.";
        Assert.True(wrapper.IsFailed);
        Assert.Equal("The download failed.", wrapper.FailureText);
        Assert.False(wrapper.CanCancel);
        Assert.Equal("Cool Mod: Failed", wrapper.AutomationText);
    }

    [Fact]
    public void The_appended_row_shows_the_target_profile_label()
    {
        var queue = new FakeModDownloadQueue();
        var target = Guid.NewGuid();
        var item = Item(null, targetProfileId: target);
        var wrapper = new DownloadRowViewModel(Localization, queue, item);

        Assert.Equal("Cool Mod", wrapper.DisplayName);
        Assert.Equal("for profile Alpha", wrapper.ProfileLabel);
    }

    [Fact]
    public async Task A_join_pulse_re_fires_and_decays()
    {
        var queue = new FakeModDownloadQueue();
        var item = Item(null);
        var wrapper = new DownloadRowViewModel(
            Localization, queue, item, pulseDecay: TimeSpan.FromMilliseconds(15));

        var raised = new List<string>();
        wrapper.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        item.Pulse++;

        Assert.Contains(nameof(DownloadRowViewModel.JoinPulse), raised);
        Assert.Equal(1, wrapper.JoinPulse);
        Assert.True(wrapper.IsPulsed);

        // The flash decays after the pulse window; a fresh pulse re-lights.
        await Task.Delay(80);
        Assert.False(wrapper.IsPulsed);

        item.Pulse++;
        Assert.True(wrapper.IsPulsed);
        Assert.Equal(2, wrapper.JoinPulse);
    }

    [Fact]
    public void A_resolved_name_forwards_to_the_wrapper()
    {
        var queue = new FakeModDownloadQueue();
        var item = Item(null, name: "Nexus mod #101");
        var wrapper = new DownloadRowViewModel(Localization, queue, item);

        Assert.Equal("Nexus mod #101", wrapper.DisplayName);
        item.DisplayName = "Resolved Name";
        Assert.Equal("Resolved Name", wrapper.DisplayName);
    }

    // ---- the structural guarantee --------------------------------------------

    [Fact]
    public void Reorders_commit_through_the_mod_rows_with_downloads_present()
    {
        var (profiles, profile, repo, session) = SeedProfile("A", "B", "C");
        var queue = new FakeModDownloadQueue();
        var vm = Build(profiles, session, repo, queue);

        // One appended download (a container the profile does not reference)
        // + one in-place morph: both coexist with a live reorder surface.
        queue.Add(Item(Guid.NewGuid()));
        var morphItem = queue.Add(Item(Row(vm, "B").ContainerId));
        Assert.NotNull(Row(vm, "B").ActiveDownload);

        vm.CommitReorderCommand.Execute(new ReorderRequest(Row(vm, "A").ContainerId, 1));

        // The planner saw only the profile rows: one service call, the
        // stored order moved, and both download renderings survived the
        // post-commit reload untouched.
        var order = Assert.Single(profiles.SetModOrderCalls);
        Assert.Equal(
            [Row(vm, "B").ContainerId, Row(vm, "A").ContainerId, Row(vm, "C").ContainerId],
            order);
        Assert.Equal(3, vm.VisibleMods.Count);
        Assert.All(vm.VisibleMods, row => Assert.IsType<ModItemViewModel>(row));
        Assert.Same(morphItem, Row(vm, "B").ActiveDownload!.Item);
        Assert.Single(vm.DownloadRows);
    }
}
