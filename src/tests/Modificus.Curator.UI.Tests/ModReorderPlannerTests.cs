using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The pure visibility-aware reorder planner: one row moved within the visible
/// subsequence while locked rows keep their exact indices, hidden rows never
/// anchor the insertion but shift at most one slot (keeping their relative
/// order), and the all-visible input reproduces the pure lock-aware
/// projection. Invalid requests (locked / hidden / missing source,
/// out-of-range rank, no-op) return null without any service involvement.
/// </summary>
public sealed class ModReorderPlannerTests
{
    /// <summary>
    /// Builds planner rows from (id, locked, visible) tuples in display order.
    /// Ids are distinct single chars for readable expectations.
    /// </summary>
    private static List<(Guid ContainerId, bool Locked, bool Visible)> Rows(
        params (char Id, bool Locked, bool Visible)[] rows) =>
        rows.Select(r => (ContainerId: G(r.Id), r.Locked, r.Visible)).ToList();

    /// <summary>A deterministic distinct Guid per char, for readable setups.</summary>
    private static Guid G(char id)
    {
        var bytes = new byte[16];
        bytes[0] = (byte)id;
        return new Guid(bytes);
    }

    private static ReorderRequest Move(char source, int targetRank) =>
        new(G(source), targetRank);

    // ---- all-visible degenerate: the pure lock-aware projection ------------

    [Fact]
    public void All_visible_reproduces_the_lock_aware_projection_with_interleaved_locks()
    {
        // [L0, A, L2, B, C]; C to rank 0 -> [L0, C, L2, A, B] (the same order
        // the pre-projection planner produced; locked slots keep their ids).
        var rows = Rows(
            ('L', true, true), ('A', false, true), ('M', true, true),
            ('B', false, true), ('C', false, true));
        var l = G('L');
        var m = G('M');

        var full = ModReorderPlanner.BuildFullOrder(rows, Move('C', 0));

        Assert.NotNull(full);
        Assert.Equal([l, G('C'), m, G('A'), G('B')], full);
    }

    [Fact]
    public void All_visible_move_past_a_lock_keeps_the_locks_exact_indices()
    {
        // [A, L, B]; B up -> [B, L, A]. L stays at index 1.
        var rows = Rows(('A', false, true), ('L', true, true), ('B', false, true));

        var full = ModReorderPlanner.BuildFullOrder(rows, Move('B', 0));

        Assert.NotNull(full);
        Assert.Equal([G('B'), G('L'), G('A')], full);
    }

    [Fact]
    public void All_visible_same_rank_is_a_noop()
    {
        var rows = Rows(('A', false, true), ('B', false, true));

        Assert.Null(ModReorderPlanner.BuildFullOrder(rows, Move('B', 1)));
    }

    [Fact]
    public void All_visible_out_of_range_rank_is_rejected()
    {
        var rows = Rows(('A', false, true), ('B', false, true));

        Assert.Null(ModReorderPlanner.BuildFullOrder(rows, Move('A', 2)));
        Assert.Null(ModReorderPlanner.BuildFullOrder(rows, Move('A', -1)));
    }

    // ---- reorder through hidden rows ----------------------------------------

    [Fact]
    public void Move_up_across_a_hidden_row_lands_the_source_adjacent_in_the_stored_order()
    {
        // [A, H(hidden), B]; B up -> [B, A, H]. The source lands immediately
        // before the visible row it crossed; H shifts exactly one slot and
        // keeps its place after A.
        var rows = Rows(('A', false, true), ('H', false, false), ('B', false, true));

        var full = ModReorderPlanner.BuildFullOrder(rows, Move('B', 0));

        Assert.NotNull(full);
        Assert.Equal([G('B'), G('A'), G('H')], full);
    }

    [Fact]
    public void Move_down_across_a_hidden_row_lands_the_source_adjacent_in_the_stored_order()
    {
        // [A, H(hidden), B]; A down (drop at end) -> [H, B, A]. The source
        // lands immediately after B; H shifts exactly one slot (up, from the
        // removal) and keeps the leading stretch.
        var rows = Rows(('A', false, true), ('H', false, false), ('B', false, true));

        var full = ModReorderPlanner.BuildFullOrder(rows, Move('A', 1));

        Assert.NotNull(full);
        Assert.Equal([G('H'), G('B'), G('A')], full);
    }

    [Fact]
    public void Drop_at_end_with_trailing_hidden_rows_settles_them_below_the_source()
    {
        // [A, B, H1, H2]; A drop at end -> [B, A, H1, H2]. The source lands
        // immediately after the last visible row (B); both hidden rows shift
        // one slot and keep their relative order.
        var rows = Rows(
            ('A', false, true), ('B', false, true), ('H', false, false), ('I', false, false));

        var full = ModReorderPlanner.BuildFullOrder(rows, Move('A', 1));

        Assert.NotNull(full);
        Assert.Equal([G('B'), G('A'), G('H'), G('I')], full);
    }

    [Fact]
    public void Hidden_rows_between_anchors_never_become_insertion_anchors()
    {
        // [A, H, B, C] with H hidden; A down one visible rank (before C? no:
        // before the visible-unlocked other at rank 1, which is C only when
        // moving past B... A at rank 0, target rank 1 = drop at end) is the
        // trailing case above; here target the middle: B up past A.
        // [A, H, B, C]; B up (rank 0) -> [B, A, H, C]: the insertion lands
        // immediately before A (the anchor), above H even though H was above B.
        var rows = Rows(
            ('A', false, true), ('H', false, false), ('B', false, true), ('C', false, true));

        var full = ModReorderPlanner.BuildFullOrder(rows, Move('B', 0));

        Assert.NotNull(full);
        Assert.Equal([G('B'), G('A'), G('H'), G('C')], full);
    }

    [Fact]
    public void Locked_rows_keep_exact_indices_while_hidden_rows_shift()
    {
        // [L, A, H, B]; B up (rank 0) -> [L, B, A, H]. L keeps index 0; the
        // hidden row shifts one slot below the crossed visible pair.
        var rows = Rows(
            ('L', true, true), ('A', false, true), ('H', false, false), ('B', false, true));

        var full = ModReorderPlanner.BuildFullOrder(rows, Move('B', 0));

        Assert.NotNull(full);
        Assert.Equal([G('L'), G('B'), G('A'), G('H')], full);
    }

    [Fact]
    public void A_visible_locked_row_between_unlocked_rows_keeps_its_index()
    {
        // [A, Lv(visible + locked), B]; B up -> [B, Lv, A]. The locked row is
        // never a destination and never moves.
        var rows = Rows(('A', false, true), ('L', true, true), ('B', false, true));

        var full = ModReorderPlanner.BuildFullOrder(rows, Move('B', 0));

        Assert.NotNull(full);
        Assert.Equal([G('B'), G('L'), G('A')], full);
    }

    [Fact]
    public void Single_visible_unlocked_row_cannot_move()
    {
        // [A, H, L]: A is the only visible unlocked row; every legal target
        // rank (only 0) reproduces its own position -> no-op.
        var rows = Rows(('A', false, true), ('H', false, false), ('L', true, true));

        Assert.Null(ModReorderPlanner.BuildFullOrder(rows, Move('A', 0)));
    }

    [Fact]
    public void Noop_when_the_rebuilt_order_equals_the_current_order()
    {
        // [A, B, H]; A dropped at its own rank 0 -> the rebuild reproduces the
        // current order (the hidden row sits below the source already).
        var rows = Rows(('A', false, true), ('B', false, true), ('H', false, false));

        Assert.Null(ModReorderPlanner.BuildFullOrder(rows, Move('A', 0)));
    }

    // ---- rejection cases -----------------------------------------------------

    [Fact]
    public void Hidden_source_is_rejected()
    {
        var rows = Rows(('A', false, true), ('H', false, false), ('B', false, true));

        Assert.Null(ModReorderPlanner.BuildFullOrder(rows, Move('H', 0)));
    }

    [Fact]
    public void Locked_source_is_rejected()
    {
        var rows = Rows(('L', true, true), ('A', false, true));

        Assert.Null(ModReorderPlanner.BuildFullOrder(rows, Move('L', 0)));
    }

    [Fact]
    public void Missing_source_is_rejected()
    {
        var rows = Rows(('A', false, true), ('B', false, true));

        Assert.Null(ModReorderPlanner.BuildFullOrder(rows, Move('X', 0)));
    }

    [Fact]
    public void Target_rank_counts_visible_unlocked_others_only()
    {
        // [A, H(hidden), B]; moving A: the only visible unlocked other is B,
        // so the valid range is [0, 1]. Rank 2 would be valid without the
        // filter (two unlocked others) but is out of range with H hidden.
        var rows = Rows(('A', false, true), ('H', false, false), ('B', false, true));

        Assert.NotNull(ModReorderPlanner.BuildFullOrder(rows, Move('A', 1)));
        Assert.Null(ModReorderPlanner.BuildFullOrder(rows, Move('A', 2)));
    }

    [Fact]
    public void Empty_rows_is_rejected()
    {
        Assert.Null(ModReorderPlanner.BuildFullOrder(
            new List<(Guid, bool, bool)>(), Move('A', 0)));
    }
}
