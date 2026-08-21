namespace Modificus.Curator.UI.Views;

/// <summary>
/// Pure, Avalonia-free math for the mod-list drag-reorder gesture, kept separate
/// from <see cref="ModListView"/>'s code-behind so it is unit-testable without a
/// real pointer pipeline. The non-obvious rules each method documents:
/// the drag threshold is inclusive (an exact <see cref="DragThresholdDip"/>-DIP
/// move engages; below stays a tap); the target rank excludes the source, all
/// locked rows, and all rows hidden by the mod-list filter/search (the realized
/// rows ARE the visible rows, so the gesture computes ranks in visible-unlocked
/// space and the commit maps them onto the stored order); the marker anchors
/// before the target slot for an upward move and after it for a downward move;
/// the edge auto-scroll clamps the offset to
/// <c>[0, scrollBarMaximum]</c>.
/// </summary>
internal static class ReorderGestureMath
{
    /// <summary>
    /// The minimum pointer movement (DIP, Euclidean) that engages a drag. At or
    /// beyond this distance the press becomes a reorder drag; below it the press
    /// is treated as a tap (no reorder, no marker).
    /// </summary>
    public const double DragThresholdDip = 8.0;

    /// <summary>
    /// The height of the top + bottom viewport edge bands that trigger
    /// auto-scroll while dragging. Chosen inside the 40-48 DIP range so the band
    /// is reachable on a touch screen without overshooting a single row.
    /// </summary>
    public const double EdgeBandDip = 44.0;

    /// <summary>
    /// The vertical scroll applied per auto-scroll tick (DIP). Modest so the list
    /// tracks the pointer without jumping past rows; the tick rate is owned by
    /// the code-behind's <c>DispatcherTimer</c>.
    /// </summary>
    public const double AutoScrollStepDip = 16.0;

    /// <summary>
    /// Whether the cumulative movement (<paramref name="deltaX"/>,
    /// <paramref name="deltaY"/>) from the press point reaches the drag
    /// threshold. Inclusive: an exact <see cref="DragThresholdDip"/>-DIP move
    /// engages the drag; anything strictly below does not. Uses squared distance
    /// so no <c>sqrt</c> is needed.
    /// </summary>
    /// <param name="deltaX">Pointer X minus press X (DIP).</param>
    /// <param name="deltaY">Pointer Y minus press Y (DIP).</param>
    /// <returns><c>true</c> when the drag should engage.</returns>
    public static bool ExceedsThreshold(double deltaX, double deltaY)
    {
        var distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
        var thresholdSquared = DragThresholdDip * DragThresholdDip;
        return distanceSquared >= thresholdSquared;
    }

    /// <summary>
    /// Computes the target unlocked insertion rank for a pointer at
    /// <paramref name="pointerY"/>. <paramref name="othersCenterY"/> holds the
    /// vertical centers of every OTHER unlocked row (the dragged source is
    /// excluded; locked rows never appear), sorted ascending by Y. The rank is
    /// the count of those centers strictly above the pointer, so a pointer
    /// between two centers lands between them. Returns an insertion rank in
    /// <c>[0, othersCenterY.Count]</c>.
    /// </summary>
    /// <param name="othersCenterY">Ascending vertical centers of the other
    /// unlocked rows (source excluded, locked excluded). Unsorted input is
    /// tolerated: the count still reflects rows strictly above the pointer only
    /// when the input is ascending; callers should pass ascending.</param>
    /// <param name="pointerY">The pointer's Y in the same coordinate space as
    /// the centers.</param>
    /// <returns>The target unlocked insertion rank.</returns>
    public static int ComputeTargetUnlockedRank(IReadOnlyList<double> othersCenterY, double pointerY)
    {
        ArgumentNullException.ThrowIfNull(othersCenterY);

        var rank = 0;
        foreach (var centerY in othersCenterY)
        {
            if (centerY < pointerY)
            {
                rank++;
            }
        }

        return rank;
    }

    /// <summary>
    /// Describes where to draw the insertion marker: the unlocked rank of the
    /// row the line anchors to, and whether the line is drawn BEFORE (above) or
    /// AFTER (below) that row.
    /// </summary>
    public readonly record struct ReorderMarker(int AnchorUnlockedRank, bool Before);

    /// <summary>
    /// Resolves the insertion marker for a computed target rank. For an upward
    /// move (<paramref name="targetRank"/> below the source's
    /// <paramref name="sourceRank"/>) the marker anchors to the row currently
    /// occupying the target unlocked slot, drawn before it. For a downward move
    /// it anchors to that same row, drawn after it. A no-op target (equal to the
    /// source rank) returns <c>null</c>, meaning the marker is cleared.
    /// </summary>
    /// <param name="sourceRank">The dragged row's current unlocked rank.</param>
    /// <param name="targetRank">The computed target unlocked rank.</param>
    /// <returns>The marker descriptor, or <c>null</c> for a no-op target.</returns>
    public static ReorderMarker? ComputeMarker(int sourceRank, int targetRank)
    {
        if (targetRank == sourceRank)
        {
            return null;
        }

        // The row currently occupying the target unlocked slot is the anchor.
        // Before it when moving up, after it when moving down.
        return new ReorderMarker(targetRank, targetRank < sourceRank);
    }

    /// <summary>
    /// The signed auto-scroll delta (DIP, positive scrolls the content down /
    /// toward the bottom) for a pointer at <paramref name="pointerY"/> inside a
    /// viewport of <paramref name="viewportHeight"/>. Returns 0 (no scroll) when
    /// the pointer is outside either edge band, including when the viewport is
    /// too short to contain a band.
    /// </summary>
    /// <param name="pointerY">The pointer Y relative to the ScrollViewer
    /// viewport (0 at the top edge).</param>
    /// <param name="viewportHeight">The ScrollViewer viewport height (DIP).</param>
    /// <returns>The per-tick scroll delta; negative scrolls up, positive scrolls
    /// down.</returns>
    public static double ComputeAutoScrollDelta(double pointerY, double viewportHeight)
    {
        if (viewportHeight <= 0)
        {
            return 0;
        }

        // Top edge band: scroll content up (delta negative moves the offset
        // toward 0, bringing lower rows into view).
        if (pointerY >= 0 && pointerY < EdgeBandDip)
        {
            return -AutoScrollStepDip;
        }

        // Bottom edge band: scroll content down.
        if (pointerY <= viewportHeight && pointerY > viewportHeight - EdgeBandDip)
        {
            return AutoScrollStepDip;
        }

        return 0;
    }

    /// <summary>
    /// Clamps a candidate scroll offset to the scrollable range
    /// <c>[0, scrollBarMaximum]</c>. A negative maximum (no scrollable content)
    /// collapses to 0, so the offset stays 0.
    /// </summary>
    /// <param name="offset">The candidate offset.</param>
    /// <param name="scrollBarMaximum">The ScrollViewer's
    /// <c>ScrollBarMaximum</c> for this axis.</param>
    /// <returns>The clamped offset.</returns>
    public static double ClampOffset(double offset, double scrollBarMaximum)
    {
        var max = scrollBarMaximum < 0 ? 0 : scrollBarMaximum;
        if (offset < 0)
        {
            return 0;
        }

        return offset > max ? max : offset;
    }

    /// <summary>
    /// The vertical render-translate offset (DIP) for the lifted drag row, so the
    /// realized item container follows the pointer while its layout slot stays
    /// reserved (render transform does not affect layout). The pointer delta moves
    /// the row with the grab (it feels grabbed where pressed, not snapped to its
    /// center); the scroll delta compensates for edge auto-scroll, because
    /// increasing the ScrollViewer offset scrolls normal content up, so the lifted
    /// row needs an equal positive transform to stay under a stationary viewport
    /// pointer. Horizontal translation stays zero (vertical reorder only).
    /// </summary>
    /// <param name="pointerY">The pointer's current Y in the ScrollViewer's
    /// viewport coordinate space (the same space as
    /// <paramref name="pressPointerY"/>).</param>
    /// <param name="pressPointerY">The pointer's Y at press (preserves the grab
    /// offset).</param>
    /// <param name="scrollOffsetY">The ScrollViewer's current vertical offset.
    /// </param>
    /// <param name="pressScrollOffsetY">The ScrollViewer's vertical offset
    /// captured at drag start.</param>
    /// <returns>The vertical translation: pointer delta plus scroll-offset delta.
    /// </returns>
    public static double ComputeLiftTranslationY(
        double pointerY,
        double pressPointerY,
        double scrollOffsetY,
        double pressScrollOffsetY)
        => (pointerY - pressPointerY) + (scrollOffsetY - pressScrollOffsetY);
}
