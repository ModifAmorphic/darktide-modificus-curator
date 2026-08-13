namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// An immutable request to move one mod row to a target rank among the unlocked
/// rows. Produced by the drag-reorder gesture (and by the Move Up / Move Down
/// commands, which compute an adjacent target rank) and consumed by the
/// mod-list VM's commit command. <see cref="TargetUnlockedRank"/> is a rank in
/// unlocked space (locked rows are skipped), so it is directly comparable across
/// reorders.
/// </summary>
/// <param name="SourceContainerId">The container id of the row being moved.
/// Must reference an unlocked row present in the current list.</param>
/// <param name="TargetUnlockedRank">The desired unlocked rank after the move, in
/// <c>[0, unlockedCount - 1]</c>. A value equal to the source's current unlocked
/// rank is a no-op.</param>
public readonly record struct ReorderRequest(Guid SourceContainerId, int TargetUnlockedRank);

/// <summary>
/// Pure reorder planning: builds the full container-id order that moves one
/// unlocked row to a target unlocked rank while every locked row keeps its
/// current zero-based position. Stateless + Avalonia-free so the lock-aware
/// projection is unit-testable independently of the VM. The backend
/// (<c>IProfileService.SetModOrder</c>) enforces the same projection as a safety
/// net; this helper constructs the exact legal order up front so the VM commits
/// exactly once with a known-good sequence.
/// </summary>
internal static class ModReorderPlanner
{
    /// <summary>
    /// Builds the full container-id order for <paramref name="request"/> against
    /// the current rows, or returns <c>null</c> when the request is invalid or a
    /// no-op (so a caller can reject without a service call).
    /// </summary>
    /// <param name="rows">The current rows in display order, each carrying its
    /// container id + locked flag.</param>
    /// <param name="request">The move request (source + target unlocked
    /// rank).</param>
    /// <returns>The full container-id order (locked rows keep their slots, the
    /// source moved to the target unlocked rank), or <c>null</c> if the source
    /// is locked / missing, the target rank is out of range, or the move yields
    /// the current order (no-op).</returns>
    public static List<Guid>? BuildFullOrder(
        IReadOnlyList<(Guid ContainerId, bool Locked)> rows,
        ReorderRequest request)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // Index the unlocked slots (and remember each locked slot holds its id).
        var unlockedIndices = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].Locked)
            {
                unlockedIndices.Add(i);
            }
        }

        if (unlockedIndices.Count == 0)
        {
            return null;
        }

        if (request.TargetUnlockedRank < 0 || request.TargetUnlockedRank >= unlockedIndices.Count)
        {
            return null;
        }

        // Locate the source among the unlocked rows. Locked or missing => reject.
        var sourceUnlockedRank = -1;
        for (var j = 0; j < unlockedIndices.Count; j++)
        {
            if (rows[unlockedIndices[j]].ContainerId == request.SourceContainerId)
            {
                sourceUnlockedRank = j;
                break;
            }
        }

        if (sourceUnlockedRank < 0)
        {
            return null;
        }

        // No-op: source stays at its current unlocked rank.
        if (sourceUnlockedRank == request.TargetUnlockedRank)
        {
            return null;
        }

        // Build the desired unlocked order: remove the source, reinsert at the
        // target rank. Then walk every slot: locked slots keep their id; open
        // slots take the next desired-unlocked id.
        var desiredUnlocked = new List<Guid>(unlockedIndices.Count);
        for (var j = 0; j < unlockedIndices.Count; j++)
        {
            if (j != sourceUnlockedRank)
            {
                desiredUnlocked.Add(rows[unlockedIndices[j]].ContainerId);
            }
        }

        desiredUnlocked.Insert(request.TargetUnlockedRank, request.SourceContainerId);

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
                full.Add(desiredUnlocked[cursor++]);
            }
        }

        return full;
    }
}
