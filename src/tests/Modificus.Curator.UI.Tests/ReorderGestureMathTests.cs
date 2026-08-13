using Modificus.Curator.UI.Views;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Pure math for the mod-list drag-reorder gesture: the 8-DIP threshold
/// (inclusive, exact boundary included), the target unlocked rank (locked rows
/// + the source excluded), the insertion-marker direction (before / after / none),
/// and the edge-band auto-scroll direction + offset clamp. No Avalonia
/// infrastructure; <see cref="ReorderGestureMath"/> is Avalonia-free.
/// </summary>
public sealed class ReorderGestureMathTests
{
    // ---- threshold (inclusive: distance >= 8 DIP engages) -------------------

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(7, 0, false)]
    [InlineData(0, 7, false)]
    [InlineData(5, 5, false)]   // sqrt(50) ~ 7.07 < 8
    [InlineData(8, 0, true)]    // exact boundary -> engages
    [InlineData(0, 8, true)]    // exact boundary -> engages
    [InlineData(8.0001, 0, true)]
    [InlineData(6, 6, true)]    // sqrt(72) ~ 8.49 >= 8
    public void ExceedsThreshold_is_inclusive_at_eight_dip(double dx, double dy, bool expected)
    {
        Assert.Equal(expected, ReorderGestureMath.ExceedsThreshold(dx, dy));
    }

    // ---- target unlocked rank ----------------------------------------------

    [Fact]
    public void Target_rank_counts_other_unlocked_centers_strictly_above_pointer()
    {
        // Three OTHER unlocked rows (source excluded) at Y = 10, 30, 50.
        var centers = new List<double> { 10, 30, 50 };

        // Pointer above all -> rank 0 (source goes to the top).
        Assert.Equal(0, ReorderGestureMath.ComputeTargetUnlockedRank(centers, pointerY: 5));
        // Between first + second centers -> rank 1 (after the row at 10).
        Assert.Equal(1, ReorderGestureMath.ComputeTargetUnlockedRank(centers, pointerY: 20));
        // Exactly on a center: that center is NOT strictly above, so it counts as
        // below -> source lands before it.
        Assert.Equal(1, ReorderGestureMath.ComputeTargetUnlockedRank(centers, pointerY: 30));
        // Just below the second center -> rank 2.
        Assert.Equal(2, ReorderGestureMath.ComputeTargetUnlockedRank(centers, pointerY: 31));
        // Below all -> rank 3 (source goes to the bottom).
        Assert.Equal(3, ReorderGestureMath.ComputeTargetUnlockedRank(centers, pointerY: 60));
    }

    [Fact]
    public void Target_rank_with_no_other_unlocked_rows_is_zero()
    {
        // The source is the only unlocked row: drop anywhere yields rank 0.
        Assert.Equal(0, ReorderGestureMath.ComputeTargetUnlockedRank(
            Array.Empty<double>(), pointerY: 100));
    }

    // ---- insertion marker ---------------------------------------------------

    [Fact]
    public void Marker_is_null_for_a_no_op_target()
    {
        Assert.Null(ReorderGestureMath.ComputeMarker(sourceRank: 1, targetRank: 1));
    }

    [Fact]
    public void Marker_for_an_upward_move_anchors_to_target_rank_drawn_before()
    {
        // Source rank 3, target 1 (moving up): before the row currently at rank 1.
        var marker = ReorderGestureMath.ComputeMarker(sourceRank: 3, targetRank: 1);
        Assert.True(marker.HasValue);
        Assert.Equal(1, marker.Value.AnchorUnlockedRank);
        Assert.True(marker.Value.Before);
    }

    [Fact]
    public void Marker_for_a_downward_move_anchors_to_target_rank_drawn_after()
    {
        // Source rank 0, target 2 (moving down): after the row currently at rank 2.
        var marker = ReorderGestureMath.ComputeMarker(sourceRank: 0, targetRank: 2);
        Assert.True(marker.HasValue);
        Assert.Equal(2, marker.GetValueOrDefault().AnchorUnlockedRank);
        Assert.False(marker.GetValueOrDefault().Before);
    }

    [Fact]
    public void Marker_for_a_one_step_down_move()
    {
        // Source rank 1, target 2: after the row currently at rank 2.
        var marker = ReorderGestureMath.ComputeMarker(sourceRank: 1, targetRank: 2);
        Assert.True(marker.HasValue);
        Assert.Equal(2, marker.GetValueOrDefault().AnchorUnlockedRank);
        Assert.False(marker.GetValueOrDefault().Before);
    }

    [Fact]
    public void Marker_for_a_one_step_up_move()
    {
        // Source rank 2, target 1: before the row currently at rank 1.
        var marker = ReorderGestureMath.ComputeMarker(sourceRank: 2, targetRank: 1);
        Assert.True(marker.HasValue);
        Assert.Equal(1, marker.GetValueOrDefault().AnchorUnlockedRank);
        Assert.True(marker.GetValueOrDefault().Before);
    }

    // ---- edge auto-scroll ---------------------------------------------------

    [Fact]
    public void AutoScroll_scrolls_up_in_the_top_edge_band()
    {
        // Viewport 600; band 44. Pointer Y in [0, 44) scrolls up (negative delta).
        Assert.Equal(-ReorderGestureMath.AutoScrollStepDip,
            ReorderGestureMath.ComputeAutoScrollDelta(pointerY: 0, viewportHeight: 600));
        Assert.Equal(-ReorderGestureMath.AutoScrollStepDip,
            ReorderGestureMath.ComputeAutoScrollDelta(pointerY: 43, viewportHeight: 600));
    }

    [Fact]
    public void AutoScroll_scrolls_down_in_the_bottom_edge_band()
    {
        // Viewport 600; band 44. Pointer Y in (556, 600] scrolls down.
        Assert.Equal(ReorderGestureMath.AutoScrollStepDip,
            ReorderGestureMath.ComputeAutoScrollDelta(pointerY: 600, viewportHeight: 600));
        Assert.Equal(ReorderGestureMath.AutoScrollStepDip,
            ReorderGestureMath.ComputeAutoScrollDelta(pointerY: 557, viewportHeight: 600));
    }

    [Fact]
    public void AutoScroll_is_zero_outside_the_bands()
    {
        // Middle of a 600-tall viewport: no scroll.
        Assert.Equal(0, ReorderGestureMath.ComputeAutoScrollDelta(pointerY: 300, viewportHeight: 600));
        // Just inside the top boundary (44) is the first non-scrolling Y.
        Assert.Equal(0, ReorderGestureMath.ComputeAutoScrollDelta(pointerY: 44, viewportHeight: 600));
        // Just inside the bottom boundary (556) is the last non-scrolling Y.
        Assert.Equal(0, ReorderGestureMath.ComputeAutoScrollDelta(pointerY: 556, viewportHeight: 600));
    }

    [Fact]
    public void AutoScroll_is_zero_for_a_non_positive_viewport()
    {
        Assert.Equal(0, ReorderGestureMath.ComputeAutoScrollDelta(pointerY: 5, viewportHeight: 0));
        Assert.Equal(0, ReorderGestureMath.ComputeAutoScrollDelta(pointerY: 5, viewportHeight: -10));
    }

    // ---- offset clamp -------------------------------------------------------

    [Theory]
    [InlineData(-5, 100, 0)]      // negative clamps to 0
    [InlineData(50, 100, 50)]     // within range stays
    [InlineData(100, 100, 100)]   // exact max stays
    [InlineData(150, 100, 100)]   // above max clamps to max
    [InlineData(50, 0, 0)]        // zero-max viewport stays 0
    [InlineData(50, -20, 0)]      // negative max collapses to 0
    public void ClampOffset_clamps_to_zero_through_max(double offset, double max, double expected)
    {
        Assert.Equal(expected, ReorderGestureMath.ClampOffset(offset, max));
    }

    // ---- lift translation (pointer delta + scroll-offset delta) -------------

    [Fact]
    public void LiftTranslation_is_zero_when_pointer_and_scroll_are_unchanged()
    {
        Assert.Equal(0, ReorderGestureMath.ComputeLiftTranslationY(
            pointerY: 100, pressPointerY: 100, scrollOffsetY: 50, pressScrollOffsetY: 50));
    }

    [Fact]
    public void LiftTranslation_follows_pointer_displacement_with_no_scroll()
    {
        // Pointer moves down 30 with no scroll => row moves down 30.
        Assert.Equal(30, ReorderGestureMath.ComputeLiftTranslationY(
            pointerY: 130, pressPointerY: 100, scrollOffsetY: 50, pressScrollOffsetY: 50));
        // Pointer moves up 25 => row moves up 25 (negative).
        Assert.Equal(-25, ReorderGestureMath.ComputeLiftTranslationY(
            pointerY: 75, pressPointerY: 100, scrollOffsetY: 50, pressScrollOffsetY: 50));
    }

    [Fact]
    public void LiftTranslation_compensates_scroll_with_a_stationary_pointer()
    {
        // Pointer stationary; content auto-scrolled down 40 (offset grew). Normal
        // content moves up, so the lifted row needs +40 to stay under the pointer.
        Assert.Equal(40, ReorderGestureMath.ComputeLiftTranslationY(
            pointerY: 100, pressPointerY: 100, scrollOffsetY: 90, pressScrollOffsetY: 50));
        // Scrolled up (offset shrank) => negative compensation.
        Assert.Equal(-40, ReorderGestureMath.ComputeLiftTranslationY(
            pointerY: 100, pressPointerY: 100, scrollOffsetY: 10, pressScrollOffsetY: 50));
    }

    [Fact]
    public void LiftTranslation_combines_pointer_and_scroll_deltas()
    {
        // Pointer down 20 AND content scrolled down 30 => row moves down 50.
        Assert.Equal(50, ReorderGestureMath.ComputeLiftTranslationY(
            pointerY: 120, pressPointerY: 100, scrollOffsetY: 80, pressScrollOffsetY: 50));
        // Pointer up 15 + content scrolled down 10 => net -5.
        Assert.Equal(-5, ReorderGestureMath.ComputeLiftTranslationY(
            pointerY: 85, pressPointerY: 100, scrollOffsetY: 60, pressScrollOffsetY: 50));
    }

    [Theory]
    [InlineData(0, 0, 0)]       // nothing moves
    [InlineData(10, 0, 10)]     // pointer-only down
    [InlineData(-10, 0, -10)]   // pointer-only up
    [InlineData(0, 7, 7)]       // scroll-only compensation
    [InlineData(0, -7, -7)]     // scroll-only other direction
    [InlineData(8, 4, 12)]      // both down
    [InlineData(-8, -4, -12)]   // both up
    public void LiftTranslation_theory(double pointerDelta, double scrollDelta, double expected)
    {
        // pressPointerY + pressScrollOffsetY anchored at 100/50; the deltas are
        // applied to current values.
        Assert.Equal(expected, ReorderGestureMath.ComputeLiftTranslationY(
            pointerY: 100 + pointerDelta, pressPointerY: 100,
            scrollOffsetY: 50 + scrollDelta, pressScrollOffsetY: 50));
    }
}
