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
/// Profile-scoped load-order locks surfaced through the mod-list VM: OrderLocked
/// carried on reload, per-row move/grip availability, the toggle-lock command
/// (independent per profile, no pending-change flag), the lock-aware Move Up /
/// Move Down (skip locked rows, locked-first-stays-first), and the drag-reorder
/// commit command (first / middle / last unlocked rank with multiple locks; one
/// SetModOrder call; exact final order; rejection of same-rank / invalid-rank /
/// locked-source / missing-source / no-active-profile requests). The fake
/// profile service honors the production lock projection so these are
/// LSP-faithful.
/// </summary>
public sealed class ModListOrderLockTests
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
    /// Seeds a profile + repository with named mods in the given (locked) order.
    /// The repository containers carry the exact ids used in the profile so the
    /// VM joins source + name on reload. Returns everything the test needs.
    /// </summary>
    private static (FakeProfileService Profiles, ProfileSummary Profile, FakeModRepository Repo)
        SeedProfile(string[] names, bool[] locked)
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
                Enabled = true,
                Order = i,
                OrderLocked = locked[i],
                Policy = ModVersionPolicy.Latest,
            };
        }

        profiles.WithMods(profile.Id, entries);
        return (profiles, profile, repo);
    }

    private static ModListViewModel BuildView(
        FakeProfileService profiles, ProfileSummary profile, FakeModRepository repo)
        => Build(profiles, new FakeProfileSession { ActiveProfileId = profile.Id }, repo);

    // ---- reload carries OrderLocked + availability --------------------------

    [Fact]
    public void Reload_carries_OrderLocked_and_move_grip_availability()
    {
        var (profiles, profile, repo) = SeedProfile(["L", "A", "B"], locked: [true, false, false]);
        var vm = BuildView(profiles, profile, repo);

        // Locked row: grip disabled, both move buttons disabled.
        var l = Row(vm, "L");
        Assert.True(l.OrderLocked);
        Assert.False(l.IsGripEnabled);
        Assert.False(l.CanMoveUp);
        Assert.False(l.CanMoveDown);

        // First unlocked row (A): move-up disabled (no unlocked row above), move-down enabled.
        var a = Row(vm, "A");
        Assert.False(a.OrderLocked);
        Assert.True(a.IsGripEnabled);
        Assert.False(a.CanMoveUp);
        Assert.True(a.CanMoveDown);

        // Last unlocked row (B): move-up enabled, move-down disabled.
        var b = Row(vm, "B");
        Assert.True(b.IsGripEnabled);
        Assert.True(b.CanMoveUp);
        Assert.False(b.CanMoveDown);
    }

    [Fact]
    public void Reload_with_all_unlocked_sets_boundary_availability()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "B", "C"], locked: [false, false, false]);
        var vm = BuildView(profiles, profile, repo);

        Assert.False(Row(vm, "A").CanMoveUp);
        Assert.True(Row(vm, "A").CanMoveDown);
        Assert.True(Row(vm, "B").CanMoveUp);
        Assert.True(Row(vm, "B").CanMoveDown);
        Assert.True(Row(vm, "C").CanMoveUp);
        Assert.False(Row(vm, "C").CanMoveDown);
    }

    // ---- toggle lock --------------------------------------------------------

    [Fact]
    public void Toggle_lock_persists_through_the_fake_reloads_and_does_not_mark_pending()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "B"], locked: [false, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        var row = Row(vm, "A");
        Assert.False(row.OrderLocked);

        session.HasPendingChanges = false;
        vm.ToggleOrderLockCommand.Execute(row);

        var call = Assert.Single(profiles.SetModOrderLockedCalls);
        Assert.Equal(profile.Id, call.Id);
        Assert.Equal(row.ContainerId, call.ContainerId);
        Assert.True(call.OrderLocked);
        // Lock metadata alone does NOT mark the session pending.
        Assert.False(session.HasPendingChanges);
        // The reload reflects the new lock state.
        Assert.True(Row(vm, "A").OrderLocked);
    }

    [Fact]
    public void Toggle_lock_off_persists_false_and_does_not_mark_pending()
    {
        var (profiles, profile, repo) = SeedProfile(["A"], locked: [true]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        Assert.True(Row(vm, "A").OrderLocked);
        session.HasPendingChanges = false;

        vm.ToggleOrderLockCommand.Execute(Row(vm, "A"));

        Assert.False(Assert.Single(profiles.SetModOrderLockedCalls).OrderLocked);
        Assert.False(Row(vm, "A").OrderLocked);
        Assert.False(session.HasPendingChanges);
    }

    [Fact]
    public void Toggle_lock_with_no_active_profile_is_a_noop()
    {
        var profiles = TestDoubles.Profiles();
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null }, new FakeModRepository());

        vm.ToggleOrderLockCommand.Execute(new ModItemViewModel(
            Localization, Guid.NewGuid(), "X", new UntrackedSource(), "", true, 0,
            ModVersionPolicy.Latest, Array.Empty<ModVersion>(), true));

        Assert.Empty(profiles.SetModOrderLockedCalls);
    }

    // ---- locked row button/reorder no-op -----------------------------------

    [Fact]
    public void Move_commands_on_a_locked_row_are_noops()
    {
        var (profiles, profile, repo) = SeedProfile(["L", "A"], locked: [true, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        session.HasPendingChanges = false;
        vm.MoveUpCommand.Execute(Row(vm, "L"));
        vm.MoveDownCommand.Execute(Row(vm, "L"));

        Assert.Empty(profiles.SetModOrderCalls);
        Assert.False(session.HasPendingChanges);
    }

    [Fact]
    public void Drag_commit_of_a_locked_source_is_rejected_without_service_call()
    {
        var (profiles, profile, repo) = SeedProfile(["L", "A"], locked: [true, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        session.HasPendingChanges = false;
        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "L"), TargetUnlockedRank: 0));

        Assert.Empty(profiles.SetModOrderCalls);
        Assert.False(session.HasPendingChanges);
    }

    // ---- move up / down skip locked rows ------------------------------------

    [Fact]
    public void MoveDown_skips_a_locked_row_and_locked_first_stays_first()
    {
        // [L0, A1, B2]; move A down one unlocked rank -> [L0, B1, A2]. L stays first.
        var (profiles, profile, repo) = SeedProfile(["L", "A", "B"], locked: [true, false, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        session.HasPendingChanges = false;
        vm.MoveDownCommand.Execute(Row(vm, "A"));

        var call = Assert.Single(profiles.SetModOrderCalls);
        Assert.Equal([Id(vm, "L"), Id(vm, "B"), Id(vm, "A")], call);
        Assert.True(session.HasPendingChanges);
        Assert.Equal("L", vm.Mods[0].Name);
        Assert.Equal("B", vm.Mods[1].Name);
        Assert.Equal("A", vm.Mods[2].Name);
        Assert.True(vm.Mods[0].OrderLocked);   // lock preserved through reload
    }

    [Fact]
    public void MoveUp_skips_a_locked_row_crossing_it()
    {
        // [A0, L1, B2]; move B up one unlocked rank -> [B0, L1, A2]. L stays at index 1.
        var (profiles, profile, repo) = SeedProfile(["A", "L", "B"], locked: [false, true, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        vm.MoveUpCommand.Execute(Row(vm, "B"));

        Assert.Equal([Id(vm, "B"), Id(vm, "L"), Id(vm, "A")], Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal("B", vm.Mods[0].Name);
        Assert.Equal("L", vm.Mods[1].Name);
        Assert.Equal("A", vm.Mods[2].Name);
        Assert.True(vm.Mods[1].OrderLocked);
    }

    [Fact]
    public void MoveUp_at_the_top_unlocked_rank_is_a_noop_even_with_locks_below()
    {
        // [A0(unlocked-top), L1, B2]; moving A up has no unlocked row above it.
        var (profiles, profile, repo) = SeedProfile(["A", "L", "B"], locked: [false, true, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        session.HasPendingChanges = false;
        vm.MoveUpCommand.Execute(Row(vm, "A"));

        Assert.Empty(profiles.SetModOrderCalls);
        Assert.False(session.HasPendingChanges);
    }

    // ---- drag commit: first / middle / last unlocked rank with locks --------

    [Fact]
    public void Drag_commit_to_first_unlocked_rank_with_multiple_locks()
    {
        // [L0, A1, L2, B3, C4]; drag C to unlocked rank 0 -> [L0, C, L2, A, B].
        var (profiles, profile, repo) = SeedProfile(
            ["L0", "A", "L2", "B", "C"], locked: [true, false, true, false, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        session.HasPendingChanges = false;
        // C is unlocked rank 2; target rank 0.
        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "C"), 0));

        var call = Assert.Single(profiles.SetModOrderCalls);
        Assert.Equal(
            [Id(vm, "L0"), Id(vm, "C"), Id(vm, "L2"), Id(vm, "A"), Id(vm, "B")],
            call);
        Assert.True(session.HasPendingChanges);
        Assert.Equal("L0", vm.Mods[0].Name);
        Assert.Equal("C", vm.Mods[1].Name);
        Assert.Equal("L2", vm.Mods[2].Name);
        Assert.Equal("A", vm.Mods[3].Name);
        Assert.Equal("B", vm.Mods[4].Name);
        Assert.True(vm.Mods[0].OrderLocked);
        Assert.True(vm.Mods[2].OrderLocked);
    }

    [Fact]
    public void Drag_commit_to_middle_unlocked_rank_with_multiple_locks()
    {
        // [L0, A1, L2, B3, C4]; drag C to unlocked rank 1 (between A and B)
        // -> [L0, A, L2, C, B].
        var (profiles, profile, repo) = SeedProfile(
            ["L0", "A", "L2", "B", "C"], locked: [true, false, true, false, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "C"), 1));

        Assert.Equal(
            [Id(vm, "L0"), Id(vm, "A"), Id(vm, "L2"), Id(vm, "C"), Id(vm, "B")],
            Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal("C", vm.Mods[3].Name);
    }

    [Fact]
    public void Drag_commit_to_last_unlocked_rank_with_multiple_locks()
    {
        // [L0, A1, L2, B3, C4]; drag A to unlocked rank 2 (last) -> [L0, B, L2, C, A].
        var (profiles, profile, repo) = SeedProfile(
            ["L0", "A", "L2", "B", "C"], locked: [true, false, true, false, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "A"), 2));

        Assert.Equal(
            [Id(vm, "L0"), Id(vm, "B"), Id(vm, "L2"), Id(vm, "C"), Id(vm, "A")],
            Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal("A", vm.Mods[4].Name);
    }

    // ---- drag commit: rejection cases --------------------------------------

    [Fact]
    public void Drag_commit_same_rank_is_a_noop()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "B", "C"], locked: [false, false, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        // B is unlocked rank 1; target rank 1 -> no-op.
        session.HasPendingChanges = false;
        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "B"), 1));

        Assert.Empty(profiles.SetModOrderCalls);
        Assert.False(session.HasPendingChanges);
    }

    [Fact]
    public void Drag_commit_invalid_rank_is_rejected()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "B"], locked: [false, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        session.HasPendingChanges = false;
        // Two unlocked rows => valid ranks 0..1; rank 5 is out of range.
        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "A"), 5));
        // Negative rank too.
        vm.CommitReorderCommand.Execute(new ReorderRequest(Id(vm, "A"), -1));

        Assert.Empty(profiles.SetModOrderCalls);
        Assert.False(session.HasPendingChanges);
    }

    [Fact]
    public void Drag_commit_missing_source_is_rejected()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "B"], locked: [false, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        vm.CommitReorderCommand.Execute(new ReorderRequest(Guid.NewGuid(), 0));

        Assert.Empty(profiles.SetModOrderCalls);
    }

    [Fact]
    public void Drag_commit_with_no_active_profile_is_rejected()
    {
        var profiles = TestDoubles.Profiles();
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null }, new FakeModRepository());

        vm.CommitReorderCommand.Execute(new ReorderRequest(Guid.NewGuid(), 0));

        Assert.Empty(profiles.SetModOrderCalls);
    }

    // ---- existing no-lock move regression -----------------------------------

    [Fact]
    public void Move_down_with_no_locks_swaps_with_the_successor()
    {
        var (profiles, profile, repo) = SeedProfile(["A", "B"], locked: [false, false]);
        var session = new FakeProfileSession { ActiveProfileId = profile.Id };
        var vm = Build(profiles, session, repo);

        vm.MoveDownCommand.Execute(Row(vm, "A"));

        Assert.Equal([Id(vm, "B"), Id(vm, "A")], Assert.Single(profiles.SetModOrderCalls));
        Assert.True(session.HasPendingChanges);
    }

    // ---- FakeProfileService mirrors production compaction/re-baselining ------
    //
    // The UI fake's AddMod + RemoveMod mirror the production lock-aware
    // compaction so the VM tests above are LSP-faithful. These guard against the
    // fake drifting from production again.

    [Fact]
    public void Fake_AddMod_compacts_non_dense_survivors_and_appends_unlocked()
    {
        // Seed a profile with non-dense survivor orders; AddMod must compact them
        // dense by stable Order sort, then append the new entry unlocked.
        var profile = Profile("Alpha");
        var profiles = TestDoubles.Profiles(profile);
        var repo = new FakeModRepository();
        var a = repo.Seed(new UntrackedSource(), "A", "1.0");
        var b = repo.Seed(new UntrackedSource(), "B", "1.0");
        profiles.WithMods(profile.Id,
            new ModListEntry { ContainerId = a.Id, Enabled = true, Order = 0, OrderLocked = true },
            new ModListEntry { ContainerId = b.Id, Enabled = true, Order = 7 }); // non-dense gap
        var c = repo.Seed(new UntrackedSource(), "C", "1.0");

        profiles.AddMod(profile.Id, c.Id, ModVersionPolicy.Latest);

        var mods = profiles.GetModList(profile.Id);
        Assert.Equal([0, 1, 2], mods.Select(m => m.Order).ToArray());
        Assert.Equal([a.Id, b.Id, c.Id], mods.Select(m => m.ContainerId).ToArray());
        Assert.True(mods[0].OrderLocked);   // lock metadata survived compaction
        Assert.False(mods[^1].OrderLocked); // new entry appended unlocked
    }

    [Fact]
    public void Fake_RemoveMod_compacts_survivors_and_rebaselines_locks()
    {
        // [L0, A1, L2, B3]; RemoveMod A => [L0, L1, B2]. The surviving L (was
        // index 2) is re-baselined to index 1.
        var profile = Profile("Alpha");
        var profiles = TestDoubles.Profiles(profile);
        var repo = new FakeModRepository();
        var l0 = repo.Seed(new UntrackedSource(), "L0", "1.0");
        var a = repo.Seed(new UntrackedSource(), "A", "1.0");
        var l2 = repo.Seed(new UntrackedSource(), "L2", "1.0");
        var b = repo.Seed(new UntrackedSource(), "B", "1.0");
        profiles.WithMods(profile.Id,
            new ModListEntry { ContainerId = l0.Id, Enabled = true, Order = 0, OrderLocked = true },
            new ModListEntry { ContainerId = a.Id, Enabled = true, Order = 1 },
            new ModListEntry { ContainerId = l2.Id, Enabled = true, Order = 2, OrderLocked = true },
            new ModListEntry { ContainerId = b.Id, Enabled = true, Order = 3 });

        profiles.RemoveMod(profile.Id, a.Id);

        var mods = profiles.GetModList(profile.Id);
        Assert.Equal([l0.Id, l2.Id, b.Id], mods.Select(m => m.ContainerId).ToArray());
        Assert.Equal([0, 1, 2], mods.Select(m => m.Order).ToArray());
        Assert.True(mods[0].OrderLocked);
        Assert.True(mods[1].OrderLocked);   // surviving L re-baselined to index 1
        Assert.False(mods[2].OrderLocked);
    }
}
