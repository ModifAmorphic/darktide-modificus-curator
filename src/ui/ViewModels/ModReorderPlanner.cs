namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// An immutable request to move one mod row to a target rank among the VISIBLE
/// unlocked rows. Produced by the drag-reorder gesture (and by the Move Up /
/// Move Down commands, which compute an adjacent target rank) and consumed by
/// the mod-list VM's commit command. <see cref="TargetUnlockedRank"/> is an
/// insertion rank in visible-unlocked space: the source lands immediately
/// before the visible-unlocked OTHER row at that rank (or immediately after
/// the last one when the rank equals the visible-unlocked-other count, the
/// drop-at-end). Locked and filter-hidden rows are never targets, so the rank
/// is directly comparable across reorders.
/// </summary>
/// <param name="SourceContainerId">The container id of the row being moved.
/// Must reference a visible, unlocked row present in the current list.</param>
/// <param name="TargetUnlockedRank">The desired insertion rank among the
/// visible-unlocked OTHER rows, in <c>[0, visibleUnlockedOtherCount]</c>. A
/// value that reproduces the current order is a no-op.</param>
public readonly record struct ReorderRequest(Guid SourceContainerId, int TargetUnlockedRank);

/// <summary>
/// Pure reorder planning: builds the full container-id order that moves one
/// visible unlocked row to a target rank among the visible unlocked rows while
/// every locked row keeps its current zero-based position. Stateless +
/// Avalonia-free so the lock- and visibility-aware projection is unit-testable
/// independently of the VM. The backend (<c>IProfileService.SetModOrder</c>)
/// enforces the same lock projection as a safety net; this helper constructs
/// the exact legal order up front so the VM commits exactly once with a
/// known-good sequence.
/// </summary>
/// <remarks>
/// <para>The move is a remove + insert over the stream of non-locked rows
/// (visible unlocked + hidden unlocked, in current order): the source is
/// removed, then reinserted immediately before the visible-unlocked other row
/// at the target rank, or immediately after the last visible-unlocked row on a
/// drop-at-end. Locked rows are pinned at their exact indices throughout.</para>
/// <para>Consequences: a hidden row never anchors the insertion (it is not a
/// drop target) but may shift by one slot as the source passes it, and it
/// keeps its relative order among the hidden rows; the source lands adjacent
/// (in the stored order) to the visible row it crossed. When nothing is
/// filtered the stream is exactly the unlocked rows, so the construction
/// reproduces the pure lock-aware projection unchanged.</para>
/// </remarks>
internal static class ModReorderPlanner
{
    /// <summary>
    /// Builds the full container-id order for <paramref name="request"/> against
    /// the current rows, or returns <c>null</c> when the request is invalid or a
    /// no-op (so a caller can reject without a service call).
    /// </summary>
    /// <param name="rows">The current rows in display order, each carrying its
    /// container id, locked flag, and whether it is visible under the current
    /// mod-list filter/search.</param>
    /// <param name="request">The move request (source + target insertion rank
    /// among the visible unlocked other rows).</param>
    /// <returns>The full container-id order (locked rows keep their exact
    /// slots, the source moved within the visible subsequence, hidden rows
    /// shifted at most one slot), or <c>null</c> if the source is locked,
    /// hidden, or missing, the target rank is out of range, or the move yields
    /// the current order (no-op).</returns>
    public static List<Guid>? BuildFullOrder(
        IReadOnlyList<(Guid ContainerId, bool Locked, bool Visible)> rows,
        ReorderRequest request)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // The movable stream: every non-locked row. Locked slots are pinned at
        // their exact indices and are excluded up front.
        var stream = new List<int>(rows.Count);
        var sourceStreamPos = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Locked)
            {
                continue;
            }

            if (rows[i].ContainerId == request.SourceContainerId)
            {
                sourceStreamPos = stream.Count;
            }

            stream.Add(i);
        }

        // The source must be present, unlocked, and visible: a locked or hidden
        // source cannot move within the visible subsequence.
        if (sourceStreamPos < 0 || !rows[stream[sourceStreamPos]].Visible)
        {
            return null;
        }

        // The insertion anchors: the visible unlocked OTHER rows, with each
        // one's position in the source-free stream (everything after the source
        // shifts left by one once it is removed).
        var anchorStreamIndex = new List<int>();
        var trimmedPos = 0;
        for (var j = 0; j < stream.Count; j++)
        {
            if (j == sourceStreamPos)
            {
                continue;
            }

            if (rows[stream[j]].Visible)
            {
                anchorStreamIndex.Add(trimmedPos);
            }

            trimmedPos++;
        }

        var anchorCount = anchorStreamIndex.Count;
        if (request.TargetUnlockedRank < 0 || request.TargetUnlockedRank > anchorCount)
        {
            return null;
        }

        // Insertion index in the source-free stream: immediately before the
        // anchor at the target rank, or immediately after the last anchor on a
        // drop-at-end. With no anchors at all (the source is the only visible
        // unlocked row) every legal target reproduces the source's own
        // position, so the move is a no-op.
        int insertAt;
        if (anchorCount == 0)
        {
            insertAt = sourceStreamPos;
        }
        else if (request.TargetUnlockedRank == anchorCount)
        {
            insertAt = anchorStreamIndex[^1] + 1;
        }
        else
        {
            insertAt = anchorStreamIndex[request.TargetUnlockedRank];
        }

        // Remove the source, reinsert at the insertion index.
        var newStream = new List<Guid>(stream.Count);
        for (var j = 0; j < stream.Count; j++)
        {
            if (j != sourceStreamPos)
            {
                newStream.Add(rows[stream[j]].ContainerId);
            }
        }

        newStream.Insert(insertAt, request.SourceContainerId);

        // No-op: the rebuilt stream reproduces the current order (the pinned
        // locked slots are untouched, so stream equality is full equality).
        var unchanged = true;
        for (var j = 0; j < stream.Count && unchanged; j++)
        {
            unchanged = rows[stream[j]].ContainerId == newStream[j];
        }

        if (unchanged)
        {
            return null;
        }

        // Walk every slot: locked slots keep their id; stream slots take the
        // next rebuilt-stream id.
        var full = new List<Guid>(rows.Count);
        var cursor = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Locked)
            {
                full.Add(rows[i].ContainerId);
            }
            else
            {
                full.Add(newStream[cursor++]);
            }
        }

        return full;
    }
}
